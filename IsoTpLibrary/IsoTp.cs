using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using ZLG.CAN;

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

        private bool IsoTpTimeAfter(uint a, uint b) => b < a;

        private byte isotp_ms_to_st_min(byte ms) => ms > 0x7F ? (byte)0x7F : ms;

        private byte isotp_st_min_to_ms(byte st_min)
        {
            if (st_min >= 0xF1 && st_min <= 0xF9) return 1;
            if (st_min <= 0x7F) return st_min;
            return 0;
        }

        /// <summary>
        /// 将所需的有效载荷长度，映射为 CAN FD 硬件允许的离散 DLC 长度
        /// </summary>
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

        /// <summary>
        /// 创建带有固定填充字节的数组
        /// </summary>
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
            // 流控帧长度随发送能力对齐
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
            // 遵从 ISO 15765-2:2016 跳变规则：
            // 如果允许的链路通道字节数 > 8 且 数据长度 >= 7，单帧必须使用 2 字节 PCI
            if (link.TxDl > 8 && link.SendSize >= 7)
            {
                pciLen = 2;
            }

            int totalFrameLen = GetCanFdDlcLength(link.SendSize + pciLen);
            // 防止超出当前配置通道的最大限制（大厂强校验）
            if (totalFrameLen > link.TxDl) totalFrameLen = link.TxDl;

            byte[] txBuf = CreatePaddedBuffer(totalFrameLen);

            if (pciLen == 1)
            {
                txBuf[0] = (byte)(((byte)IsoTpPCIType.SINGLE << 4) | (link.SendSize & 0x0F));
                Array.Copy(link.SendBuffer, 0, txBuf, 1, link.SendSize);
            }
            else // 2 字节 PCI
            {
                txBuf[0] = (byte)((byte)IsoTpPCIType.SINGLE << 4); // 高4位为0，低4位固定为0
                txBuf[1] = (byte)link.SendSize;                  // 第二字节存放真实长度
                Array.Copy(link.SendBuffer, 0, txBuf, 2, link.SendSize);
            }

            var isSend = SendCan(id, Channel, txBuf);
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendFirstFrame(uint id)
        {
            int pciLen = 2;
            bool isEscapeFrame = false;

            // 如果长度超过了传统 12 位（4095字节）的限制，触发 4 字节大首帧（本处做常规 CAN FD 兼容）
            if (link.SendSize > 4095)
            {
                pciLen = 6;
                isEscapeFrame = true;
            }

            // 首帧也需要填充对齐到离散 DLC 长度
            int totalFrameLen = GetCanFdDlcLength(link.TxDl);
            byte[] txBuf = CreatePaddedBuffer(totalFrameLen);

            if (!isEscapeFrame)
            {
                txBuf[0] = (byte)(((byte)IsoTpPCIType.FIRST_FRAME << 4) | (byte)(0x0F & (link.SendSize >> 8)));
                txBuf[1] = (byte)(link.SendSize & 0xFF);
            }
            else
            {
                txBuf[0] = (byte)((byte)IsoTpPCIType.FIRST_FRAME << 4); // 传统位填0
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

            // 计算当前这一帧需要打包的实际总长度（1字节PCI + 剩余数据）
            int requiredFrameLen = remainingDataLen + pciLen;

            // 限制单帧不能超过通信通道配置的 TX_DL 限制
            if (requiredFrameLen > link.TxDl)
            {
                requiredFrameLen = link.TxDl;
            }

            // 映射为底层 CAN FD 硬件支持的离散填充长度
            int totalFrameLen = GetCanFdDlcLength(requiredFrameLen);
            byte[] txBuf = CreatePaddedBuffer(totalFrameLen);

            // 组连续帧 PCI
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

        public void OnCanMessage(byte[] data, byte len)
        {
            if (len < 1 || len > 64) return;

            byte pciType = (byte)((data[0] & 0xF0) >> 4);

            switch (pciType)
            {
                case (byte)IsoTpPCIType.SINGLE:
                    int sfDl = data[0] & 0x0F;
                    int sfDataOffset = 1;

                    // 大厂对 CAN FD 单帧的长度解析逻辑
                    if (sfDl == 0)
                    {
                        if (len < 2) return;
                        sfDl = data[1]; // 2字节PCI，第2字节为长度
                        sfDataOffset = 2;
                    }

                    if (sfDl == 0 || sfDl > (len - sfDataOffset))
                    {
                        Console.WriteLine("Single-frame length error.");
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
                    int ffDl = (data[0] & 0x0F) << 8 | data[1];
                    int ffDataOffset = 2;

                    if (ffDl == 0) // 说明是长于 4095 字节的扩展首帧
                    {
                        if (len < 6) return;
                        ffDl = (data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5];
                        ffDataOffset = 6;
                    }

                    // 效仿大厂对接收流控前的环境进行极限判断
                    int currentFfPayloadLen = len - ffDataOffset;
                    if (ffDl <= currentFfPayloadLen)
                    {
                        Console.WriteLine("Should not use multiple frame transmission.");
                        return;
                    }
                    if (ffDl > link.ReceiveBufSize)
                    {
                        Console.WriteLine("Multi-frame response too large.");
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
                    if (link.ReceiveStatus != IsoTpReceiveStatus.InProgress)
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

                    int cfPayloadLen = len - 1; // 除去 1 字节 PCI 的可用有效硬件长度
                    int remainingBytes = link.ReceiveSize - link.ReceiveOffset;

                    // 裁剪防止溢出
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
                    if (link.SendStatus != IsoTpSendStatus.InProgress) break;

                    byte fs = (byte)(data[0] & 0x0F);
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
                    }
                    else if (fs == (byte)IsoTpPCIFlowStatus.CONTINUE)
                    {
                        link.SendBsRemain = (bs == 0) ? InvalidBs : bs;
                        link.SendStMin = isotp_st_min_to_ms(stMin);
                        link.SendWtfCount = 0;

                        // 核心机制演进：根据接收方流控帧发过来的硬件数据真实长度 len，自动动态降级/更新发送端的 TxDl
                        // 确保发送的 CF 长度不会撑死物理硬件接收能力较弱的下游 ECU
                        if (len < link.TxDl)
                        {
                            link.TxDl = len;
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
            IsoTpReturnCode ret;
            if (link == null) return IsoTpReturnCode.ERROR;
            if (size > link.SendBufSize) return IsoTpReturnCode.OVERFLOW;
            if (link.SendStatus == IsoTpSendStatus.InProgress) return IsoTpReturnCode.INPROGRESS;

            link.SendSize = size;
            link.SendOffset = 0;
            Array.Copy(payload, link.SendBuffer, size);

            // 【大厂核心解耦判断】：当前能够容纳的最大单帧空间
            int maxSingleFramePayload = (link.TxDl > 8) ? (link.TxDl - 2) : 7;

            if (link.SendSize <= maxSingleFramePayload)
            {
                ret = SendSingleFrame(id);
            }
            else
            {
                ret = SendFirstFrame(id);
            }

            if (ret == IsoTpReturnCode.OK)
            {
                link.SendBsRemain = 0;
                link.SendStMin = 0;
                link.SendWtfCount = 0;
                link.SendTimerSt = isotp_user_get_ms();
                link.SendTimerBs = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                link.SendProtocolResult = IsoTpProtocolResult.OK;
                link.SendStatus = IsoTpSendStatus.InProgress;
            }
            return ret;
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

            // 默认设置为经典 8 字节链路环境，可在初始化后外部变更为 64
            link.TxDl = 8;
            link.PaddingEnable = true;
            link.PaddingByte = 0xCC;
        }

        public void Poll()
        {
            IsoTpReturnCode ret;
            if (link.SendStatus == IsoTpSendStatus.InProgress)
            {
                if ((link.SendBsRemain == InvalidBs || link.SendBsRemain > 0)
                    && (link.SendStMin == 0 || (0 != link.SendStMin && IsoTpTimeAfter(isotp_user_get_ms(), link.SendTimerSt))))
                {
                    ret = SendConsecutiveFrame();
                    if (ret == IsoTpReturnCode.OK)
                    {
                        if (link.SendBsRemain != InvalidBs) link.SendBsRemain -= 1;
                        link.SendTimerBs = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                        link.SendTimerSt = isotp_user_get_ms() + link.SendStMin;

                        if (link.SendOffset >= link.SendSize) link.SendStatus = IsoTpSendStatus.Idle;
                    }
                    else
                    {
                        link.SendStatus = IsoTpSendStatus.Error;
                    }
                }
                if (IsoTpTimeAfter(isotp_user_get_ms(), link.SendTimerBs))
                {
                    link.SendProtocolResult = IsoTpProtocolResult.TIMEOUT_BS;
                    link.SendStatus = IsoTpSendStatus.Error;
                }
            }
            if (link.ReceiveStatus == IsoTpReceiveStatus.InProgress)
            {
                if (IsoTpTimeAfter(isotp_user_get_ms(), link.ReceiveTimerCr))
                {
                    link.ReceiveProtocolResult = IsoTpProtocolResult.TIMEOUT_CR;
                    link.ReceiveStatus = IsoTpReceiveStatus.Idle;
                }
            }
        }

        private uint isotp_user_get_ms() => (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % uint.MaxValue);
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
