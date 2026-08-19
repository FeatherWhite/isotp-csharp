using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UdsDiagnostic.Transport;

namespace UdsDiagnostic.Transport
{
    public delegate void CanFrameReceivedHandler(uint arbitrationId, byte[] payload);

    public interface ICanTransport
    {
        uint Channel { get; }
        event CanFrameReceivedHandler OnFrameReceived;
        bool SendCanFrame(uint arbitrationId, byte[] payload);
    }
}

namespace UdsDiagnostic.IsoTp
{
    public enum IsoTpPCIType : byte
    {
        SINGLE = 0,
        FIRST_FRAME = 1,
        CONSECUTIVE_FRAME = 2,
        FLOW_CONTROL_FRAME = 3
    }

    public enum IsoTpFlowStatus : byte
    {
        CONTINUE = 0,
        WAIT = 1,
        OVERFLOW = 2
    }

    public class IsoTpConfig
    {
        public uint TxId { get; set; }
        public uint RxId { get; set; }
        public int TxDl { get; set; } = 8; // 8 (Classic CAN) or 64 (CAN FD)
        public bool PaddingEnable { get; set; } = true;
        public byte PaddingByte { get; set; } = 0xCC;
        public byte DefaultBlockSize { get; set; } = 0;
        public byte DefaultStMin { get; set; } = 0; // ms
        public int TimeoutMs { get; set; } = 2000;
    }

    public class IsoTpSession : IDisposable
    {
        private readonly ICanTransport _transport;
        private readonly IsoTpConfig _config;

        private TaskCompletionSource<byte[]>? _rxTcs;
        private TaskCompletionSource<bool>? _flowControlTcs;

        private MemoryStream? _rxStream;
        private int _expectedRxSize;
        private byte _expectedSn;

        private byte _remoteBs = 0;
        private byte _remoteStMin = 0;

        public IsoTpSession(ICanTransport transport, IsoTpConfig config)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _transport.OnFrameReceived += HandleCanMessage;
        }

        #region API 接口

        /// <summary>
        /// 异步发送 ISO-TP 报文（自动处理 单帧 SF 或 多帧 FF + CF + FlowControl 交互）
        /// </summary>
        public async Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
        {
            if (payload == null || payload.Length == 0) return;

            int maxSfLen = _config.TxDl > 8 ? _config.TxDl - 2 : 7;

            if (payload.Length <= maxSfLen)
            {
                // ---- 单帧发送 (Single Frame) ----
                byte[] sfBuf = BuildSingleFrame(payload);
                if (!_transport.SendCanFrame(_config.TxId, sfBuf))
                    throw new InvalidOperationException("底层 CAN 单帧发送失败。");
            }
            else
            {
                // ---- 多帧发送 (First Frame + Flow Control + Consecutive Frames) ----
                _flowControlTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                byte[] ffBuf = BuildFirstFrame(payload, out int firstPayloadLen);
                if (!_transport.SendCanFrame(_config.TxId, ffBuf))
                    throw new InvalidOperationException("底层 CAN 首帧(FF)发送失败。");

                // 等待接收端的流控帧 (Flow Control)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_config.TimeoutMs);

                using (cts.Token.Register(() => _flowControlTcs.TrySetCanceled()))
                {
                    await _flowControlTcs.Task;
                }

                // 循环发送连续帧 (CF)
                int offset = firstPayloadLen;
                byte sn = 1;
                int bsRemain = _remoteBs;

                while (offset < payload.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 处理 STmin 延时
                    if (_remoteStMin > 0)
                    {
                        int delayMs = GetStMinInMs(_remoteStMin);
                        if (delayMs > 0)
                            await Task.Delay(delayMs, cancellationToken);
                    }

                    int pciLen = 1;
                    int remaining = payload.Length - offset;
                    int framePayloadLen = Math.Min(remaining, _config.TxDl - pciLen);
                    int totalFrameLen = GetCanFdDlcLength(framePayloadLen + pciLen);

                    byte[] cfBuf = CreatePaddedBuffer(totalFrameLen);
                    cfBuf[0] = (byte)(((byte)IsoTpPCIType.CONSECUTIVE_FRAME << 4) | (sn & 0x0F));
                    Array.Copy(payload, offset, cfBuf, pciLen, framePayloadLen);

                    if (!_transport.SendCanFrame(_config.TxId, cfBuf))
                        throw new InvalidOperationException($"连续帧 (SN={sn}) 发送失败。");

                    offset += framePayloadLen;
                    sn = (byte)((sn + 1) & 0x0F);

                    // 块大小 (BlockSize) 计数
                    if (_remoteBs > 0)
                    {
                        bsRemain--;
                        if (bsRemain == 0 && offset < payload.Length)
                        {
                            // 等待下一个流控帧
                            _flowControlTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            using var fcCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            fcCts.CancelAfter(_config.TimeoutMs);

                            using (fcCts.Token.Register(() => _flowControlTcs.TrySetCanceled()))
                            {
                                await _flowControlTcs.Task;
                            }
                            bsRemain = _remoteBs;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 异步接收完整 ISO-TP 报文
        /// </summary>
        public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            _rxTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_config.TimeoutMs);

            using (cts.Token.Register(() => _rxTcs.TrySetCanceled()))
            {
                return await _rxTcs.Task;
            }
        }

        #endregion

        #region CAN 接收分发处理 (事件驱动)

        private void HandleCanMessage(uint arbitrationId, byte[] data)
        {
            if (arbitrationId != _config.RxId || data == null || data.Length == 0) return;

            byte pciType = (byte)((data[0] & 0xF0) >> 4);

            switch ((IsoTpPCIType)pciType)
            {
                case IsoTpPCIType.SINGLE:
                    HandleSingleFrame(data);
                    break;

                case IsoTpPCIType.FIRST_FRAME:
                    HandleFirstFrame(data);
                    break;

                case IsoTpPCIType.CONSECUTIVE_FRAME:
                    HandleConsecutiveFrame(data);
                    break;

                case IsoTpPCIType.FLOW_CONTROL_FRAME:
                    HandleFlowControlFrame(data);
                    break;
            }
        }

        private void HandleSingleFrame(byte[] data)
        {
            int sfDl = data[0] & 0x0F;
            int offset = 1;

            if (sfDl == 0) // CAN FD 扩展单帧格式
            {
                if (data.Length < 2) return;
                sfDl = data[1];
                offset = 2;
            }

            if (sfDl > data.Length - offset) return;

            byte[] payload = new byte[sfDl];
            Array.Copy(data, offset, payload, 0, sfDl);

            _rxTcs?.TrySetResult(payload);
        }

        private void HandleFirstFrame(byte[] data)
        {
            int ffDl = ((data[0] & 0x0F) << 8) | data[1];
            int offset = 2;

            if (ffDl == 0) // 扩展 32-bit 长度首帧
            {
                if (data.Length < 6) return;
                ffDl = (data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5];
                offset = 6;
            }

            _expectedRxSize = ffDl;
            _rxStream = new MemoryStream(_expectedRxSize);

            int payloadLen = data.Length - offset;
            _rxStream.Write(data, offset, payloadLen);

            _expectedSn = 1;

            // 回复流控帧 (Flow Control)
            SendFlowControl(IsoTpFlowStatus.CONTINUE, _config.DefaultBlockSize, _config.DefaultStMin);
        }

        private void HandleConsecutiveFrame(byte[] data)
        {
            if (_rxStream == null) return;

            byte sn = (byte)(data[0] & 0x0F);
            if (sn != _expectedSn)
            {
                _rxTcs?.TrySetException(new InvalidDataException($"Sequence Number 错误，期望: {_expectedSn}, 实际: {sn}"));
                _rxStream = null;
                return;
            }

            int payloadLen = data.Length - 1;
            int remain = _expectedRxSize - (int)_rxStream.Length;
            int copyLen = Math.Min(payloadLen, remain);

            _rxStream.Write(data, 1, copyLen);
            _expectedSn = (byte)((_expectedSn + 1) & 0x0F);

            if (_rxStream.Length >= _expectedRxSize)
            {
                _rxTcs?.TrySetResult(_rxStream.ToArray());
                _rxStream = null;
            }
        }

        private void HandleFlowControlFrame(byte[] data)
        {
            if (data.Length < 3) return;

            byte fs = (byte)(data[0] & 0x0F);
            _remoteBs = data[1];
            _remoteStMin = data[2];

            if (fs == (byte)IsoTpFlowStatus.CONTINUE)
            {
                _flowControlTcs?.TrySetResult(true);
            }
            else if (fs == (byte)IsoTpFlowStatus.OVERFLOW)
            {
                _flowControlTcs?.TrySetException(new OverflowException("接收端返回 FlowControl OVERFLOW 溢出错误。"));
            }
        }

        private void SendFlowControl(IsoTpFlowStatus status, byte bs, byte stMin)
        {
            int frameLen = GetCanFdDlcLength(3);
            byte[] fcBuf = CreatePaddedBuffer(frameLen);

            fcBuf[0] = (byte)(((byte)IsoTpPCIType.FLOW_CONTROL_FRAME << 4) | (byte)status);
            fcBuf[1] = bs;
            fcBuf[2] = stMin;

            _transport.SendCanFrame(_config.TxId, fcBuf);
        }

        #endregion

        #region 辅助工具方法

        private byte[] BuildSingleFrame(byte[] payload)
        {
            int pciLen = (_config.TxDl > 8 && payload.Length >= 7) ? 2 : 1;
            int totalLen = GetCanFdDlcLength(payload.Length + pciLen);
            if (totalLen > _config.TxDl) totalLen = _config.TxDl;

            byte[] buf = CreatePaddedBuffer(totalLen);
            if (pciLen == 1)
            {
                buf[0] = (byte)(((byte)IsoTpPCIType.SINGLE << 4) | (payload.Length & 0x0F));
                Array.Copy(payload, 0, buf, 1, payload.Length);
            }
            else
            {
                buf[0] = (byte)((byte)IsoTpPCIType.SINGLE << 4);
                buf[1] = (byte)payload.Length;
                Array.Copy(payload, 0, buf, 2, payload.Length);
            }
            return buf;
        }

        private byte[] BuildFirstFrame(byte[] payload, out int firstPayloadLen)
        {
            bool isEscape = payload.Length > 4095;
            int pciLen = isEscape ? 6 : 2;
            int totalLen = GetCanFdDlcLength(_config.TxDl);

            byte[] buf = CreatePaddedBuffer(totalLen);
            if (!isEscape)
            {
                buf[0] = (byte)(((byte)IsoTpPCIType.FIRST_FRAME << 4) | (byte)((payload.Length >> 8) & 0x0F));
                buf[1] = (byte)(payload.Length & 0xFF);
            }
            else
            {
                buf[0] = (byte)((byte)IsoTpPCIType.FIRST_FRAME << 4);
                buf[1] = 0;
                buf[2] = (byte)((payload.Length >> 24) & 0xFF);
                buf[3] = (byte)((payload.Length >> 16) & 0xFF);
                buf[4] = (byte)((payload.Length >> 8) & 0xFF);
                buf[5] = (byte)(payload.Length & 0xFF);
            }

            firstPayloadLen = totalLen - pciLen;
            Array.Copy(payload, 0, buf, pciLen, firstPayloadLen);
            return buf;
        }

        private byte[] CreatePaddedBuffer(int length)
        {
            byte[] buf = new byte[length];
            if (_config.PaddingEnable)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    buf[i] = _config.PaddingByte;
                }
            }
            return buf;
        }

        private int GetCanFdDlcLength(int requiredSize)
        {
            if (requiredSize <= 8) return 8;
            if (requiredSize <= 12) return 12;
            if (requiredSize <= 16) return 16;
            if (requiredSize <= 20) return 20;
            if (requiredSize <= 24) return 24;
            if (requiredSize <= 32) return 32;
            if (requiredSize <= 48) return 48;
            return 64;
        }

        private int GetStMinInMs(byte stMin)
        {
            if (stMin >= 0xF1 && stMin <= 0xF9) return 1; // 100us - 900us -> Windows 系统级视作 1ms 避让
            if (stMin <= 0x7F) return stMin;
            return 0;
        }

        public void Dispose()
        {
            _transport.OnFrameReceived -= HandleCanMessage;
            _rxStream?.Dispose();
        }

        #endregion
    }
}