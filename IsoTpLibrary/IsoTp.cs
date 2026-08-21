using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using ZLG.CAN;
using System.Diagnostics;


namespace IsoTpLibrary
{
    public class IsoTp
    {
        public IsoTpLink link { get; set; } = new IsoTpLink();
        public delegate bool SendCanFunc(uint arbitrationId, uint channel, byte[] payload);
        public SendCanFunc SendCan;
        private const ushort InvalidBs = 0xFFFF;

        /// <summary>
        /// ZLG Can Channel Index
        /// </summary>
        public uint Channel { get; set; } = 0;

        /// <summary>
        /// 高精度计时：使用 Stopwatch 替代 DateTimeOffset (解决 Windows 15.6ms 时钟精度瓶颈)
        /// </summary>
        private static readonly Stopwatch _sysSw = Stopwatch.StartNew();
        private uint isotp_user_get_ms() => (uint)_sysSw.ElapsedMilliseconds;

        /// <summary>
        /// 利用 C# 有符号整型溢出特性，解决 uint 49天/取模回绕下的时间比较逻辑
        /// </summary>
        private bool IsoTpTimeAfter(uint a, uint b) => ((int)(a - b)) > 0;

        private byte isotp_ms_to_st_min(byte ms) => ms > 0x7F ? (byte)0x7F : ms;

        private bool IsValidTxDl(int txDl)
        {
            return txDl == 8 || txDl == 12 || txDl == 16 || txDl == 20 ||
                   txDl == 24 || txDl == 32 || txDl == 48 || txDl == 64;
        }

        private byte isotp_st_min_to_ms(byte st_min)
        {
            if (st_min >= 0xF1 && st_min <= 0xF9) return 1;
            if (st_min <= 0x7F) return st_min;
            return 0; // 0x80-0xF0 预留域，默认按 0ms 快速处理
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

        public IsoTpReturnCode SendFlowControl(IsoTpPCIFlowStatus flow_status, byte block_size, int st_min_ms)
        {
            if (st_min_ms < 0 || st_min_ms > byte.MaxValue) return IsoTpReturnCode.LENGTH;
            return SendFlowControl(flow_status, block_size, (byte)st_min_ms);
        }

        public IsoTpReturnCode SendFlowControl(IsoTpPCIFlowStatus flow_status, byte block_size, byte st_min_ms)
        {
            if (link == null || SendCan == null) return IsoTpReturnCode.ERROR;

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
            if (link == null || link.SendBuffer == null || SendCan == null ||
                !IsValidTxDl(link.TxDl) || link.SendSize == 0 ||
                link.SendSize > link.SendBufSize || link.SendSize > link.SendBuffer.Length)
            {
                return IsoTpReturnCode.ERROR;
            }

            int pciLen = 1;
            if (link.TxDl > 8 && link.SendSize > 7)
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
            if (!isSend)
            {
                link.SendProtocolResult = IsoTpProtocolResult.ERROR;
            }
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendFirstFrame(uint id)
        {
            if (link == null || link.SendBuffer == null || SendCan == null ||
                !IsValidTxDl(link.TxDl) || link.SendSize <= 7 ||
                link.SendSize > link.SendBufSize || link.SendSize > link.SendBuffer.Length)
            {
                return IsoTpReturnCode.ERROR;
            }

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
            if (payloadDataLen > link.SendSize)
            {
                payloadDataLen = link.SendSize;
            }
            Array.Copy(link.SendBuffer, 0, txBuf, pciLen, payloadDataLen);

            bool isSend = SendCan(id, Channel, txBuf);
            if (isSend)
            {
                link.SendOffset = (ushort)payloadDataLen;
                link.SendSn = 1;
            }
            else
            {
                link.SendProtocolResult = IsoTpProtocolResult.ERROR;
            }
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendConsecutiveFrame()
        {
            if (link == null || link.SendBuffer == null || SendCan == null ||
                !IsValidTxDl(link.TxDl) || link.SendOffset >= link.SendSize ||
                link.SendSize > link.SendBufSize || link.SendSize > link.SendBuffer.Length)
            {
                return IsoTpReturnCode.ERROR;
            }

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
            else
            {
                link.SendProtocolResult = IsoTpProtocolResult.ERROR;
            }
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode ReceiveConsecutiveFrame(IsoTpConsecutiveFrame frame, byte len)
        {
            if (len < 1 || frame.Data == null || len > frame.Data.Length)
            {
                return IsoTpReturnCode.LENGTH;
            }

            byte[] data = new byte[len + 1];
            data[0] = (byte)(((byte)IsoTpPCIType.CONSECUTIVE_FRAME << 4) | (frame.SN & 0x0F));
            Array.Copy(frame.Data, 0, data, 1, len);
            OnCanMessage(data, (byte)data.Length);

            if (link.ReceiveProtocolResult == IsoTpProtocolResult.WRONG_SN)
            {
                return IsoTpReturnCode.WRONG_SN;
            }
            if (link.ReceiveStatus == IsoTpReceiveStatus.Idle &&
                link.ReceiveProtocolResult != IsoTpProtocolResult.OK)
            {
                return IsoTpReturnCode.ERROR;
            }
            return IsoTpReturnCode.OK;
        }

        public void OnCanMessage(byte[] data, byte len)
        {
            if (data == null || len < 1 || len > 64 || len > data.Length) return;

            byte pciType = (byte)((data[0] & 0xF0) >> 4);

            switch (pciType)
            {
                case (byte)IsoTpPCIType.SINGLE:
                    // Keep an unread complete SDU intact. The adapter may fetch
                    // more than one CAN frame in a Poll cycle; accepting another
                    // complete frame here would overwrite the pending message.
                    if (link.ReceiveStatus == IsoTpReceiveStatus.Full) break;

                    int sfDl = data[0] & 0x0F;
                    int sfDataOffset = 1;

                    if (sfDl == 0)
                    {
                        if (len < 2) return;
                        sfDl = data[1];
                        sfDataOffset = 2;
                    }

                    if (sfDl == 0 || sfDl > (len - sfDataOffset) ||
                        link.ReceiveBuffer == null || sfDl > link.ReceiveBufSize ||
                        sfDl > link.ReceiveBuffer.Length)
                    {
                        //Console.WriteLine("Single-frame length or buffer error.");
                        return;
                    }

                    if (link.ReceiveStatus == IsoTpReceiveStatus.InProgress)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.UNEXP_PDU;
                    }
                    else
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.OK;
                    }

                    Array.Copy(data, sfDataOffset, link.ReceiveBuffer, 0, sfDl);
                    link.ReceiveSize = (ushort)sfDl;
                    link.ReceiveStatus = IsoTpReceiveStatus.Full;
                    break;

                case (byte)IsoTpPCIType.FIRST_FRAME:
                    if (link.ReceiveStatus == IsoTpReceiveStatus.Full) break;
                    if (len < 2) return;

                    int ffDl = (data[0] & 0x0F) << 8 | data[1];
                    int ffDataOffset = 2;

                    if (ffDl == 0)
                    {
                        if (len < 6) return;
                        ffDl = (data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5];
                        ffDataOffset = 6;
                    }

                    if (ffDl <= 0) return;
                    int currentFfPayloadLen = len - ffDataOffset;
                    if (ffDl <= currentFfPayloadLen)
                    {
                        // A First Frame must require at least one Consecutive Frame.
                        return;
                    }
                    if (link.ReceiveBuffer == null || ffDl > link.ReceiveBufSize || ffDl > link.ReceiveBuffer.Length)
                    {
                        //Console.WriteLine("Multi-frame response too large.");
                        link.ReceiveProtocolResult = IsoTpProtocolResult.BUFFER_OVFLW;
                        link.ReceiveStatus = IsoTpReceiveStatus.Idle;
                        SendFlowControl(IsoTpPCIFlowStatus.OVERFLOW, 0, 0);
                        break;
                    }

                    if (link.ReceiveStatus == IsoTpReceiveStatus.InProgress)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.UNEXP_PDU;
                    }
                    else
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.OK;
                    }

                    Array.Copy(data, ffDataOffset, link.ReceiveBuffer, 0, currentFfPayloadLen);
                    link.ReceiveSize = (ushort)ffDl;
                    link.ReceiveOffset = (ushort)currentFfPayloadLen;
                    link.ReceiveSn = 1;

                    link.ReceiveStatus = IsoTpReceiveStatus.InProgress;
                    link.ReceiveBsCount = IsoTpConfig.DefaultBlockSize;
                    SendFlowControl(IsoTpPCIFlowStatus.CONTINUE, link.ReceiveBsCount, IsoTpConfig.DefaultStMin);
                    link.ReceiveTimerCr = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                    break;

                case (byte)IsoTpPCIType.CONSECUTIVE_FRAME:
                    if (len < 2 || link.ReceiveStatus != IsoTpReceiveStatus.InProgress)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.UNEXP_PDU;
                        break;
                    }

                    byte sn = (byte)(data[0] & 0x0F);
                    if (link.ReceiveSn != sn)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.WRONG_SN;
                        link.ReceiveStatus = IsoTpReceiveStatus.Idle;
                        break;
                    }

                    int cfPayloadLen = len - 1;
                    int remainingBytes = link.ReceiveSize - link.ReceiveOffset;

                    if (cfPayloadLen > remainingBytes)
                    {
                        cfPayloadLen = remainingBytes;
                    }

                    Array.Copy(data, 1, link.ReceiveBuffer, link.ReceiveOffset, cfPayloadLen);
                    link.ReceiveOffset += (ushort)cfPayloadLen;

                    if (++(link.ReceiveSn) > 0x0F)
                    {
                        link.ReceiveSn = 0;
                    }

                    if (link.ReceiveOffset >= link.ReceiveSize)
                    {
                        link.ReceiveStatus = IsoTpReceiveStatus.Full;
                    }
                    else
                    {
                        link.ReceiveTimerCr = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                        if (--link.ReceiveBsCount == 0)
                        {
                            link.ReceiveBsCount = IsoTpConfig.DefaultBlockSize;
                            SendFlowControl(IsoTpPCIFlowStatus.CONTINUE, link.ReceiveBsCount, IsoTpConfig.DefaultStMin);
                        }
                    }
                    break;

                case (byte)IsoTpPCIType.FLOW_CONTROL_FRAME:
                    if (len < 3 || link.SendStatus != IsoTpSendStatus.WaitFlowControl) break;

                    byte fs = (byte)(data[0] & 0x0F);
                    if (fs > (byte)IsoTpPCIFlowStatus.OVERFLOW)
                    {
                        link.SendProtocolResult = IsoTpProtocolResult.INVALID_FS;
                        link.SendStatus = IsoTpSendStatus.Error;
                        break;
                    }
                    byte bs = data[1];
                    byte stMin = data[2];

                    link.SendTimerBs = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;

                    if (fs == (byte)IsoTpPCIFlowStatus.OVERFLOW)
                    {
                        link.SendProtocolResult = IsoTpProtocolResult.BUFFER_OVFLW;
                        link.SendStatus = IsoTpSendStatus.Error;
                    }
                    else if (fs == (byte)IsoTpPCIFlowStatus.WAIT)
                    {
                        link.SendWtfCount += 1;
                        if (link.SendWtfCount > IsoTpConfig.MaxWftNumber)
                        {
                            link.SendProtocolResult = IsoTpProtocolResult.WFT_OVRN;
                            link.SendStatus = IsoTpSendStatus.Error;
                        }
                        else
                        {
                            link.SendTimerBs = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                        }
                    }
                    else if (fs == (byte)IsoTpPCIFlowStatus.CONTINUE)
                    {
                        link.SendBsRemain = (bs == 0) ? InvalidBs : bs;
                        link.SendStMin = isotp_st_min_to_ms(stMin);
                        link.SendWtfCount = 0;

                        // 🌟 收到 CTS 后立即触发发送，不需要额外的 STmin 等待时间
                        link.SendTimerSt = isotp_user_get_ms();

                        link.SendStatus = IsoTpSendStatus.WaitSendOk;
                    }
                    break;
            }
        }

        public IsoTpReturnCode Send(byte[] payload, ushort size)
        {
            if (link == null) return IsoTpReturnCode.ERROR;
            return SendWithId(link.SendArbitrationId, payload, size);
        }

        public IsoTpReturnCode SendWithId(uint id, byte[] payload, ushort size)
        {
            IsoTpReturnCode ret;
            if (link == null || payload == null || link.SendBuffer == null || SendCan == null) return IsoTpReturnCode.ERROR;
            if (!IsValidTxDl(link.TxDl)) return IsoTpReturnCode.ERROR;
            if (size == 0) return IsoTpReturnCode.LENGTH;
            if (size > payload.Length || size > link.SendBufSize || size > link.SendBuffer.Length) return IsoTpReturnCode.OVERFLOW;

            if (link.SendStatus == IsoTpSendStatus.WaitFlowControl || link.SendStatus == IsoTpSendStatus.WaitSendOk)
                return IsoTpReturnCode.INPROGRESS;

            link.SendSize = size;
            link.SendOffset = 0;
            link.SendArbitrationId = id;
            Array.Copy(payload, link.SendBuffer, size);

            int maxSingleFramePayload = (link.TxDl > 8) ? (link.TxDl - 2) : 7;

            if (link.SendSize <= maxSingleFramePayload)
            {
                ret = SendSingleFrame(id);
                if (ret == IsoTpReturnCode.OK)
                {
                    link.SendStatus = IsoTpSendStatus.Idle;
                }
            }
            else
            {
                link.SendBsRemain = 0;
                link.SendStMin = 0;
                link.SendWtfCount = 0;
                link.SendTimerSt = isotp_user_get_ms();
                link.SendTimerBs = link.SendTimerSt + IsoTpConfig.DefaultResponseTimeout;
                link.SendProtocolResult = IsoTpProtocolResult.OK;

                // Set the state before invoking SendCan. A synchronous CAN
                // callback may deliver the FC while the FF is being sent.
                link.SendStatus = IsoTpSendStatus.WaitFlowControl;
                ret = SendFirstFrame(id);
                if (ret != IsoTpReturnCode.OK)
                {
                    link.SendStatus = IsoTpSendStatus.Error;
                }
            }
            return ret;
        }

        public IsoTpReturnCode Receive(byte[] payload, ushort payloadSize, ref ushort outSize)
        {
            outSize = 0;
            if (payload == null || link.ReceiveBuffer == null) return IsoTpReturnCode.ERROR;
            if (link.ReceiveStatus != IsoTpReceiveStatus.Full) return IsoTpReturnCode.NO_DATA;
            if (payloadSize > payload.Length) payloadSize = (ushort)payload.Length;
            if (link.ReceiveSize > payloadSize) return IsoTpReturnCode.OVERFLOW;

            Array.Copy(link.ReceiveBuffer, payload, link.ReceiveSize);
            outSize = link.ReceiveSize;
            link.ReceiveStatus = IsoTpReceiveStatus.Idle;
            return IsoTpReturnCode.OK;
        }

        public void InitLink(uint sendId, byte[] sendbuf, ushort sendbufSize, byte[] recvbuf, ushort recvbufSize)
        {
            if (link == null) link = new IsoTpLink();
            link.SendArbitrationId = sendId;
            link.SendBuffer = sendbuf ?? Array.Empty<byte>();
            link.ReceiveStatus = IsoTpReceiveStatus.Idle;
            link.SendStatus = IsoTpSendStatus.Idle;
            link.SendBufSize = (ushort)Math.Min(sendbufSize, link.SendBuffer.Length);
            link.ReceiveBuffer = recvbuf ?? Array.Empty<byte>();
            link.ReceiveBufSize = (ushort)Math.Min(recvbufSize, link.ReceiveBuffer.Length);
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

        /// <summary>
        /// 🌟【高性能 Burst 优化版】多帧循环轮询状态机
        /// </summary>
        public void Poll()
        {
            uint current_ms = isotp_user_get_ms();

            // 1. 连续帧 Burst 冲刺发送逻辑
            if (link.SendStatus == IsoTpSendStatus.InProgress || link.SendStatus == IsoTpSendStatus.WaitSendOk)
            {
                // 循环冲刺，直到需要等待 STmin、等待 BlockSize 允许，或底层 SendCan 挂起/出错
                while (link.SendStatus == IsoTpSendStatus.InProgress || link.SendStatus == IsoTpSendStatus.WaitSendOk)
                {
                    current_ms = isotp_user_get_ms();

                    // 检查数据是否已全部压入硬件队列
                    if (link.SendOffset >= link.SendSize)
                    {
                        link.SendStatus = IsoTpSendStatus.Idle;
                        break;
                    }

                    // 检查 BlockSize 和 STmin 时间条件
                    bool bsValid = (link.SendBsRemain == InvalidBs || link.SendBsRemain > 0);
                    bool stMinValid = (link.SendStMin == 0 || IsoTpTimeAfter(current_ms, link.SendTimerSt) || current_ms == link.SendTimerSt);

                    if (bsValid && stMinValid)
                    {
                        link.SendStatus = IsoTpSendStatus.InProgress;

                        IsoTpReturnCode ret = SendConsecutiveFrame();
                        current_ms = isotp_user_get_ms();
                        if (ret == IsoTpReturnCode.OK)
                        {
                            if (link.SendBsRemain != InvalidBs)
                            {
                                link.SendBsRemain -= 1;
                                if (link.SendBsRemain == 0 && link.SendOffset < link.SendSize)
                                {
                                    link.SendStatus = IsoTpSendStatus.WaitFlowControl;
                                }
                            }

                            // 刷新计时器
                            link.SendTimerBs = current_ms + IsoTpConfig.DefaultResponseTimeout;
                            link.SendTimerSt = current_ms + link.SendStMin;

                            if (link.SendOffset >= link.SendSize)
                            {
                                link.SendStatus = IsoTpSendStatus.Idle;
                                break;
                            }

                            // 💡 如果 STmin > 0，说明需要真实的毫秒延时，本次 Poll 的 Burst 发送暂停，等待下一次时钟到达
                            if (link.SendStMin > 0)
                            {
                                break;
                            }
                        }
                        else
                        {
                            link.SendProtocolResult = IsoTpProtocolResult.ERROR;
                            link.SendStatus = IsoTpSendStatus.Error;
                            break;
                        }
                    }
                    else
                    {
                        // 条件不满足（等待 BlockSize 或 STmin 计时），退出 Burst 循环
                        break;
                    }
                }
            }

            // N_Bs applies both while waiting for the first FC and after a finite
            // block has been exhausted and another FC is required.
            if (link.SendStatus == IsoTpSendStatus.WaitFlowControl &&
                IsoTpTimeAfter(current_ms, link.SendTimerBs))
            {
                link.SendProtocolResult = IsoTpProtocolResult.TIMEOUT_BS;
                link.SendStatus = IsoTpSendStatus.Error;
            }
            else if ((link.SendStatus == IsoTpSendStatus.InProgress ||
                      link.SendStatus == IsoTpSendStatus.WaitSendOk) &&
                     IsoTpTimeAfter(current_ms, link.SendTimerBs))
            {
                link.SendProtocolResult = IsoTpProtocolResult.TIMEOUT_BS;
                link.SendStatus = IsoTpSendStatus.Error;
            }

            // 2. 接收阶段超时检查
            if (link.ReceiveStatus == IsoTpReceiveStatus.InProgress)
            {
                if (IsoTpTimeAfter(current_ms, link.ReceiveTimerCr))
                {
                    link.ReceiveProtocolResult = IsoTpProtocolResult.TIMEOUT_CR;
                    link.ReceiveStatus = IsoTpReceiveStatus.Idle;
                }
            }
        }
    }
    public class IsoTpLink
    {
        // === 效仿 Vector 体系增加的配置项 ===
        public int TxDl { get; set; } = 8;          // 通道的发送数据长度上限 (8, 12, 16, 20, 24, 32, 48, 64)
        public bool PaddingEnable { get; set; } = true; // 是否开启填充机制
        public byte PaddingByte { get; set; } = 0xCC;  // 默认填充字节

        // Sender parameters
        public uint SendArbitrationId { get; set; }
        public byte[] SendBuffer { get; set; }
        public ushort SendBufSize { get; set; }
        public ushort SendSize { get; set; }
        public ushort SendOffset { get; set; }
        public byte SendSn { get; set; }
        public ushort SendBsRemain { get; set; }
        public byte SendStMin { get; set; }
        public byte SendWtfCount { get; set; }
        public uint SendTimerSt { get; set; }
        public uint SendTimerBs { get; set; }
        public IsoTpProtocolResult SendProtocolResult { get; set; }
        public IsoTpSendStatus SendStatus { get; set; }

        // Receiver parameters
        public uint ReceiveArbitrationId { get; set; }
        public byte[] ReceiveBuffer { get; set; }
        public ushort ReceiveBufSize { get; set; }
        public ushort ReceiveSize { get; set; }
        public ushort ReceiveOffset { get; set; }
        public byte ReceiveSn { get; set; }
        public byte ReceiveBsCount { get; set; }
        public uint ReceiveTimerCr { get; set; }
        public IsoTpProtocolResult ReceiveProtocolResult { get; set; }
        public IsoTpReceiveStatus ReceiveStatus { get; set; }
    }
}
