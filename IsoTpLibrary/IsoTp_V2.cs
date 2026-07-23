using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZLG.CAN;

namespace IsoTpLibrary
{
    public class IsoTp_V2
    {
        public IsoTpLink link { get; set; } = new IsoTpLink();
        public delegate bool SendCanFunc(uint arbitrationId, uint channel, byte[] payload);
        public SendCanFunc SendCan;
        private const ushort InvalidBs = 0xFFFF;

        /// <summary>
        /// ZLG Can Channel Index
        /// </summary>
        public uint Channel { get; set; } = 0;

        #region 🚨 新增：上层通知事件
        // 当收到完整的单帧或多帧拼接包时触发，将整个 Payload 抛给应用层
        public event Action<byte[]> OnReceiveComplete;
        // 当发送（单帧发送完毕 或 多帧全部连续帧发送完毕）成功时触发
        public event Action OnSendComplete;
        // 当协议栈内部遭遇超时或异常错误时触发
        public event Action<IsoTpProtocolResult> OnProtocolError;
        #endregion

        private readonly object _txLock = new object();
        private CancellationTokenSource _txCts;

        private byte isotp_ms_to_st_min(byte ms) => ms > 0x7F ? (byte)0x7F : ms;

        private byte isotp_st_min_to_ms(byte st_min)
        {
            if (st_min >= 0xF1 && st_min <= 0xF9) return 1; // 100us-900us 在通用 PC 端直接按 1ms 处理
            if (st_min <= 0x7F) return st_min;
            return 0;
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

        private byte[] CreatePaddedBuffer(int length)
        {
            byte[] buf = new byte[length];
            if (link.PaddingEnable)
            {
                for (int i = 0; i < length; i++) buf[i] = link.PaddingByte;
            }
            return buf;
        }

        public IsoTpReturnCode SendFlowControl(IsoTpPCIFlowStatus flow_status, byte block_size, byte st_min_ms)
        {
            int frameLen = GetCanFdDlcLength(3);
            byte[] txBuf = CreatePaddedBuffer(frameLen);

            txBuf[0] = (byte)(((byte)IsoTpPCIType.FLOW_CONTROL_FRAME << 4) | (byte)flow_status);
            txBuf[1] = block_size;
            txBuf[2] = isotp_ms_to_st_min(st_min_ms);

            var isSend = SendCan(link.SendArbitrationId, Channel, txBuf);
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendSingleFrame(uint id)
        {
            int pciLen = 1;
            if (link.TxDl > 8 && link.SendSize >= 7)
            {
                pciLen = 2;
            }

            int totalFrameLen = GetCanFdDlcLength(link.SendSize + pciLen);
            if (totalFrameLen > link.TxDl) totalFrameLen = link.TxDl;

            byte[] txBuf = CreatePaddedBuffer(totalFrameLen);

            if (pciLen == 1)
            {
                txBuf[0] = (byte)(((byte)IsoTpPCIType.SINGLE << 4) | (link.SendSize & 0x0F));
                Array.Copy(link.SendBuffer, 0, txBuf, 1, link.SendSize);
            }
            else
            {
                txBuf[0] = (byte)((byte)IsoTpPCIType.SINGLE << 4);
                txBuf[1] = (byte)link.SendSize;
                Array.Copy(link.SendBuffer, 0, txBuf, 2, link.SendSize);
            }

            var isSend = SendCan(id, Channel, txBuf);
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendFirstFrame(uint id)
        {
            int pciLen = 2;
            bool isEscapeFrame = false;

            if (link.SendSize > 4095)
            {
                pciLen = 6;
                isEscapeFrame = true;
            }

            int totalFrameLen = GetCanFdDlcLength(link.TxDl);
            byte[] txBuf = CreatePaddedBuffer(totalFrameLen);

            if (!isEscapeFrame)
            {
                txBuf[0] = (byte)(((byte)IsoTpPCIType.FIRST_FRAME << 4) | (byte)(0x0F & (link.SendSize >> 8)));
                txBuf[1] = (byte)(link.SendSize & 0xFF);
            }
            else
            {
                txBuf[0] = (byte)((byte)IsoTpPCIType.FIRST_FRAME << 4);
                txBuf[1] = 0;
                txBuf[2] = (byte)((link.SendSize >> 24) & 0xFF);
                txBuf[3] = (byte)((link.SendSize >> 16) & 0xFF);
                txBuf[4] = (byte)((link.SendSize >> 8) & 0xFF);
                txBuf[5] = (byte)(link.SendSize & 0xFF);
            }

            int payloadDataLen = totalFrameLen - pciLen;
            Array.Copy(link.SendBuffer, 0, txBuf, pciLen, payloadDataLen);

            bool isSend = SendCan(id, Channel, txBuf);
            if (isSend)
            {
                link.SendOffset = (ushort)payloadDataLen;
                link.SendSn = 1;
            }
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendConsecutiveFrame()
        {
            int pciLen = 1;
            int remainingDataLen = link.SendSize - link.SendOffset;

            int requiredFrameLen = remainingDataLen + pciLen;
            if (requiredFrameLen > link.TxDl)
            {
                requiredFrameLen = link.TxDl;
            }

            int totalFrameLen = GetCanFdDlcLength(requiredFrameLen);
            byte[] txBuf = CreatePaddedBuffer(totalFrameLen);

            txBuf[0] = (byte)(((byte)IsoTpPCIType.CONSECUTIVE_FRAME << 4) | (link.SendSn & 0x0F));

            int actualPayloadCopyLen = totalFrameLen - pciLen;
            if (actualPayloadCopyLen > remainingDataLen)
            {
                actualPayloadCopyLen = remainingDataLen;
            }

            Array.Copy(link.SendBuffer, link.SendOffset, txBuf, pciLen, actualPayloadCopyLen);

            bool isSend = SendCan(link.SendArbitrationId, Channel, txBuf);
            if (isSend)
            {
                link.SendOffset += (ushort)actualPayloadCopyLen;
                if (++(link.SendSn) > 0x0F)
                {
                    link.SendSn = 0;
                }
            }
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        /// <summary>
        /// 📁 修改定位：由周立功收包回调线程直接驱动。消灭旧 Poll，改为事件自推进。
        /// </summary>
        public void OnCanMessage(byte[] data, byte len)
        {
            if (len < 1 || len > 64) return;
            byte pciType = (byte)((data[0] & 0xF0) >> 4);

            switch (pciType)
            {
                // ... SINGLE, FIRST_FRAME, CONSECUTIVE_FRAME 的代码保持一致 ...

                case (byte)IsoTpPCIType.FLOW_CONTROL_FRAME:
                    lock (_txLock)
                    {
                        if (link.SendStatus != IsoTpSendStatus.WaitFlowControl) break;

                        byte fs = (byte)(data[0] & 0x0F);
                        byte bs = data[1];
                        byte stMin = data[2];

                        if (fs == (byte)IsoTpPCIFlowStatus.OVERFLOW)
                        {
                            link.SendStatus = IsoTpSendStatus.Error;
                            link.SendProtocolResult = IsoTpProtocolResult.BUFFER_OVFLW;
                            CancelTxTask();
                            Task.Run(() => OnProtocolError?.Invoke(IsoTpProtocolResult.BUFFER_OVFLW));
                        }
                        else if (fs == (byte)IsoTpPCIFlowStatus.WAIT)
                        {
                            link.SendWtfCount += 1;
                            if (link.SendWtfCount > IsoTpConfig.MaxWftNumber)
                            {
                                link.SendStatus = IsoTpSendStatus.Error;
                                link.SendProtocolResult = IsoTpProtocolResult.WFT_OVRN;
                                CancelTxTask();
                                Task.Run(() => OnProtocolError?.Invoke(IsoTpProtocolResult.WFT_OVRN));
                            }
                        }
                        else if (fs == (byte)IsoTpPCIFlowStatus.CONTINUE)
                        {
                            link.SendBsRemain = (bs == 0) ? InvalidBs : bs;
                            link.SendStMin = isotp_st_min_to_ms(stMin);
                            link.SendWtfCount = 0;
                            link.SendStatus = IsoTpSendStatus.WaitSendOk;

                            // 安全重置 CTS，彻底规避旧闭包引发的锁死
                            _txCts?.Cancel();
                            _txCts?.Dispose();
                            _txCts = new CancellationTokenSource();

                            StartAsyncConsecutiveSend(_txCts.Token);
                        }
                    }
                    break;
            }
        }

        public IsoTpReturnCode Send(byte[] payload, ushort size)
        {
            return SendWithId(link.SendArbitrationId, payload, size);
        }

        public IsoTpReturnCode SendWithId(uint id, byte[] payload, ushort size)
        {
            lock (_txLock)
            {
                if (link == null) return IsoTpReturnCode.ERROR;
                if (size > link.SendBufSize) return IsoTpReturnCode.OVERFLOW;

                if (link.SendStatus == IsoTpSendStatus.WaitFlowControl || link.SendStatus == IsoTpSendStatus.WaitSendOk || link.SendStatus == IsoTpSendStatus.InProgress)
                    return IsoTpReturnCode.INPROGRESS;

                CancelTxTask(); // 清理残留发送任务

                link.SendSize = size;
                link.SendOffset = 0;
                Array.Copy(payload, link.SendBuffer, size);

                int maxSingleFramePayload = (link.TxDl > 8) ? (link.TxDl - 2) : 7;

                if (link.SendSize <= maxSingleFramePayload)
                {
                    var ret = SendSingleFrame(id);
                    if (ret == IsoTpReturnCode.OK)
                    {
                        link.SendStatus = IsoTpSendStatus.Idle;
                        // 🚨 单帧发送完，直接秒通知上层：发送完成
                        Task.Run(() => OnSendComplete?.Invoke());
                    }
                    return ret;
                }
                else
                {
                    var ret = SendFirstFrame(id);
                    if (ret == IsoTpReturnCode.OK)
                    {
                        link.SendBsRemain = 0;
                        link.SendStMin = 0;
                        link.SendWtfCount = 0;
                        link.SendProtocolResult = IsoTpProtocolResult.OK;
                        link.SendStatus = IsoTpSendStatus.WaitFlowControl;

                        // 启动一个 Bs 安全定时器（超时无流控则强制退出）
                        _txCts = new CancellationTokenSource();
                        var token = _txCts.Token;
                        Task.Delay((int)IsoTpConfig.DefaultResponseTimeout, token).ContinueWith(t =>
                        {
                            lock (_txLock)
                            {
                                if (link.SendStatus == IsoTpSendStatus.WaitFlowControl)
                                {
                                    link.SendProtocolResult = IsoTpProtocolResult.TIMEOUT_BS;
                                    link.SendStatus = IsoTpSendStatus.Error;
                                    OnProtocolError?.Invoke(IsoTpProtocolResult.TIMEOUT_BS);
                                }
                            }
                        }, token);
                    }
                    return ret;
                }
            }
        }

        /// <summary>
        /// 高精度微秒/毫秒级延时（避开 Windows 15.6ms 线程调度黑洞）
        /// </summary>
        private static void PreciseDelay(int millisecondsTimeout, CancellationToken token)
        {
            if (millisecondsTimeout <= 0) return;

            // 如果延时较长（大于30ms），先进行粗略的普通等待释放 CPU
            if (millisecondsTimeout > 30)
            {
                bool canceled = token.WaitHandle.WaitOne(millisecondsTimeout - 10);
                if (canceled) return;
            }

            // 剩余最后冲刺阶段采用 Stopwatch 自旋，确保硬核精度
            var sw = Stopwatch.StartNew();
            long ticksTimeout = millisecondsTimeout * Stopwatch.Frequency / 1000;
            while (sw.ElapsedTicks < ticksTimeout)
            {
                if (token.IsCancellationRequested) return;
                Thread.SpinWait(10); // 适当释放流水线，防止 CPU 熔断
            }
        }


        /// <summary>
        /// 核心修复：单体专一职责的连续帧发送机
        /// </summary>
        private void StartAsyncConsecutiveSend(CancellationToken token)
        {
            Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        if (token.IsCancellationRequested) return;

                        lock (_txLock)
                        {
                            // 检查是否发送完成
                            if (link.SendOffset >= link.SendSize)
                            {
                                link.SendStatus = IsoTpSendStatus.Idle;
                                Task.Run(() => OnSendComplete?.Invoke());
                                return;
                            }

                            // 检查当前 Block 是否发满
                            if (link.SendBsRemain == 0)
                            {
                                link.SendStatus = IsoTpSendStatus.WaitFlowControl;
                                // 启动安全的超时检查器，直接传入当前的 token
                                StartBsTimeoutCheck(token);
                                return;
                            }
                        }

                        // 刚性 STmin 延时控制（采用高精度精准时钟）
                        if (link.SendStMin > 0)
                        {
                            PreciseDelay(link.SendStMin, token);
                        }

                        lock (_txLock)
                        {
                            if (token.IsCancellationRequested) return;
                            if (link.SendStatus != IsoTpSendStatus.WaitSendOk && link.SendStatus != IsoTpSendStatus.InProgress) return;

                            link.SendStatus = IsoTpSendStatus.InProgress;
                            var ret = SendConsecutiveFrame();
                            if (ret == IsoTpReturnCode.OK)
                            {
                                if (link.SendBsRemain != InvalidBs) link.SendBsRemain -= 1;
                            }
                            else
                            {
                                link.SendStatus = IsoTpSendStatus.Error;
                                Task.Run(() => OnProtocolError?.Invoke(IsoTpProtocolResult.ERROR));
                                return;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    lock (_txLock) { link.SendStatus = IsoTpSendStatus.Error; }
                }
            }, token);
        }

        /// <summary>
        /// 🚨 新增核心：无 Poll 架构下的多帧连续帧全自动后台发送机
        /// </summary>
        private void StartAsyncConsecutiveSend()
        {
            lock (_txLock)
            {
                _txCts?.Cancel(); // 停掉刚才的 N_Bs 超时检查
                _txCts = new CancellationTokenSource();
            }

            var token = _txCts.Token;
            Task.Run(async () =>
            {
                try
                {
                    while (link.SendOffset < link.SendSize)
                    {
                        if (token.IsCancellationRequested) return;

                        // 1. 检查 BlockSize 是否已经发满
                        if (link.SendBsRemain == 0)
                        {
                            lock (_txLock)
                            {
                                link.SendStatus = IsoTpSendStatus.WaitFlowControl;
                            }
                            // 重新开一个超时任务等待下一个流控
                            StartBsTimeoutCheck(token);
                            return;
                        }

                        // 2. STmin 刚性延时控制（采用无阻塞的 Task.Delay，不占用硬件及OpenTAP线程）
                        if (link.SendStMin > 0)
                        {
                            await Task.Delay(link.SendStMin, token);
                        }

                        lock (_txLock)
                        {
                            if (token.IsCancellationRequested) return;
                            link.SendStatus = IsoTpSendStatus.InProgress;

                            var ret = SendConsecutiveFrame();
                            if (ret == IsoTpReturnCode.OK)
                            {
                                if (link.SendBsRemain != InvalidBs) link.SendBsRemain -= 1;
                            }
                            else
                            {
                                link.SendStatus = IsoTpSendStatus.Error;
                                OnProtocolError?.Invoke(IsoTpProtocolResult.ERROR);
                                return;
                            }
                        }
                    }

                    // 3. 所有 CF 发送完毕，完美收官
                    lock (_txLock)
                    {
                        link.SendStatus = IsoTpSendStatus.Idle;
                    }
                    OnSendComplete?.Invoke();
                }
                catch (TaskCanceledException) { }
            }, token);
        }

        private void StartBsTimeoutCheck(CancellationToken token)
        {
            Task.Delay((int)IsoTpConfig.DefaultResponseTimeout, token).ContinueWith(t =>
            {
                lock (_txLock)
                {
                    if (link.SendStatus == IsoTpSendStatus.WaitFlowControl && !token.IsCancellationRequested)
                    {
                        link.SendProtocolResult = IsoTpProtocolResult.TIMEOUT_BS;
                        link.SendStatus = IsoTpSendStatus.Error;
                        OnProtocolError?.Invoke(IsoTpProtocolResult.TIMEOUT_BS);
                    }
                }
            }, token);
        }

        private void CancelTxTask()
        {
            _txCts?.Cancel();
            _txCts?.Dispose();
            _txCts = null;
        }

        public IsoTpReturnCode Receive(byte[] payload, ushort payloadSize, ref ushort outSize)
        {
            if (link.ReceiveStatus != IsoTpReceiveStatus.Full) return IsoTpReturnCode.NO_DATA;
            ushort copylen = link.ReceiveSize;
            if (copylen > payloadSize) copylen = payloadSize;
            Array.Copy(link.ReceiveBuffer, payload, copylen);
            outSize = copylen;
            link.ReceiveStatus = IsoTpReceiveStatus.Idle;
            return IsoTpReturnCode.OK;
        }

        public void InitLink(uint sendId, byte[] sendbuf, ushort sendbufSize, byte[] recvbuf, ushort recvbufSize)
        {
            CancelTxTask();
            link.SendArbitrationId = sendId;
            link.SendBuffer = sendbuf;
            link.ReceiveStatus = IsoTpReceiveStatus.Idle;
            link.SendStatus = IsoTpSendStatus.Idle;
            link.SendBufSize = sendbufSize;
            link.ReceiveBufSize = recvbufSize;
            link.ReceiveBuffer = recvbuf;
            link.SendSize = 0;
            link.SendOffset = 0;
            link.SendSn = 0;
            link.SendBsRemain = 0;
            link.SendStMin = 0;
            link.SendWtfCount = 0;
            link.SendTimerSt = 0;
            link.SendTimerBs = 0;
            link.SendProtocolResult = IsoTpProtocolResult.OK;
            link.ReceiveArbitrationId = 0;
            link.ReceiveSize = 0;
            link.ReceiveOffset = 0;
            link.ReceiveSn = 0;
            link.ReceiveBsCount = 0;
            link.ReceiveTimerCr = 0;
            link.ReceiveProtocolResult = IsoTpProtocolResult.OK;

            link.TxDl = 8;
            link.PaddingEnable = true;
            link.PaddingByte = 0xCC;
        }
    }
}
