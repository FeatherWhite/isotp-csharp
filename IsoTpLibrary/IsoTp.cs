using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace IsoTpLibrary
{
    public class IsoTp
    {
        public IsoTpLink link { get; set; } = new IsoTpLink();

        private const ushort InvalidBs = 0xFFFF;

        private bool IsoTpTimeAfter(uint a, uint b)
        {
            return b < a;
        }
        /* st_min to microsecond */
        private byte isotp_ms_to_st_min(byte ms)
        {
            byte st_min;

            st_min = ms;
            if (st_min > 0x7F)
            {
                st_min = 0x7F;
            }

            return st_min;
        }

        /* st_min to msec  */
        private byte isotp_st_min_to_ms(byte st_min)
        {
            byte ms;

            if (st_min >= 0xF1 && st_min <= 0xF9)
            {
                ms = 1;
            }
            else if (st_min <= 0x7F)
            {
                ms = st_min;
            }
            else
            {
                ms = 0;
            }

            return ms;
        }
        //public bool FramePadding { get; set; } = true;
        public IsoTpReturnCode SendFlowControl(IsoTpPCIFlowStatus flow_status, byte block_size, byte st_min_ms)
        {

            IsoTpFlowControlFrame flowControl = new IsoTpFlowControlFrame();
            IsoTpDataArray data = new IsoTpDataArray();

            /* setup message  */
            flowControl.Type = (byte)IsoTpPCIType.FLOW_CONTROL_FRAME;
            flowControl.FS = (byte)flow_status;
            flowControl.BS = block_size;
            flowControl.STmin = isotp_ms_to_st_min(st_min_ms);
            flowControl.Reserve = new byte[5];
            data.Elems = StructMapping.StructToBytes(flowControl);
            var isSend = SendCan(link.SendArbitrationId, data.Elems);
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendSingleFrame(uint id)
        {
            IsoTpSingleFrame singleFrame = new IsoTpSingleFrame();
            IsoTpDataArray data = new IsoTpDataArray();
            /* multi frame message length must greater than 7  */
            singleFrame.Type = (byte)IsoTpPCIType.SINGLE;
            singleFrame.SF_DL = (byte)link.SendSize;
            singleFrame.Data = new byte[7];
            Array.Copy(link.SendBuffer, singleFrame.Data, link.SendSize);
            data.Elems = StructMapping.StructToBytes(singleFrame);
            var isSend = SendCan(id, data.Elems);
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendFirstFrame(uint id)
        {
            IsoTpFirstFrame firstFrame = new IsoTpFirstFrame();
            IsoTpDataArray data = new IsoTpDataArray();

            /* multi frame message length must greater than 7  */
            firstFrame.Type = (byte)IsoTpPCIType.FIRST_FRAME;
            firstFrame.FF_DL_low = (byte)link.SendSize;
            firstFrame.FF_DL_high = (byte)(0x0F & (link.SendSize >> 8));
            firstFrame.Data = new byte[6];
            Array.Copy(link.SendBuffer, firstFrame.Data, firstFrame.Data.Length);
            data.Elems = StructMapping.StructToBytes(firstFrame);
            bool isSend = SendCan(id, data.Elems);

            if (isSend == true)
            {
                link.SendOffset += (ushort)firstFrame.Data.Length;
                link.SendSn = 1;
            }
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode SendConsecutiveFrame()
        {

            IsoTpConsecutiveFrame consecutiveFrame = new IsoTpConsecutiveFrame();
            IsoTpDataArray data = new IsoTpDataArray();
            ushort dataLength;
            /* multi frame message length must greater than 7  */
            consecutiveFrame.Type = (byte)IsoTpPCIType.CONSECUTIVE_FRAME;
            consecutiveFrame.SN = link.SendSn;
            dataLength = Convert.ToUInt16(link.SendSize - link.SendOffset);
            consecutiveFrame.Data = new byte[7];

            if (dataLength > consecutiveFrame.Data.Length) {
                dataLength = Convert.ToUInt16(consecutiveFrame.Data.Length);
            }

            Array.Copy(link.SendBuffer,link.SendOffset, consecutiveFrame.Data,0 ,dataLength);
            data.Elems = StructMapping.StructToBytes(consecutiveFrame);


            /* send message */
            bool isSend = SendCan(link.SendArbitrationId,data.Elems);

            if (isSend == true)
            {
                link.SendOffset += dataLength;
                if (++(link.SendSn) > 0x0F)
                {
                    link.SendSn = 0;
                }
            }
            return isSend ? IsoTpReturnCode.OK : IsoTpReturnCode.ERROR;
        }

        public IsoTpReturnCode ReceiveSingleFrame(IsoTpSingleFrame singleFrame,byte len)
        {
            if((singleFrame.SF_DL == 0) || (singleFrame.SF_DL > len - 1))
            {
                //isotp_user_debug("Single-frame length too small.");
                return IsoTpReturnCode.LENGTH;
            }
            Array.Copy(singleFrame.Data, link.ReceiveBuffer,  singleFrame.SF_DL);
            link.ReceiveSize = singleFrame.SF_DL;
            return IsoTpReturnCode.OK;
        }

        public IsoTpReturnCode ReceiveFirstFrame(IsoTpFirstFrame firstFrame, byte len)
        {
            ushort payloadLength;
            if(len != 8)
            {
                //isotp_user_debug("First frame should be 8 bytes in length.");
                return IsoTpReturnCode.LENGTH;
            }
            payloadLength = firstFrame.FF_DL_high;
            payloadLength = Convert.ToUInt16((payloadLength << 8) + firstFrame.FF_DL_low);
            if(payloadLength <= 7)
            {
                //isotp_user_debug("Should not use multiple frame transmission.");
                return IsoTpReturnCode.LENGTH;
            }
            if (payloadLength > link.ReceiveBufSize)
            {
                //isotp_user_debug("Multi-frame response too large for receiving buffer.");
                return IsoTpReturnCode.OVERFLOW;
            }
            Array.Copy(firstFrame.Data, link.ReceiveBuffer,  firstFrame.Data.Length);
            link.ReceiveSize = payloadLength;
            link.ReceiveOffset = Convert.ToUInt16(firstFrame.Data.Length);
            link.ReceiveSn = 1;
            return IsoTpReturnCode.OK;
        }

        public IsoTpReturnCode ReceiveConsecutiveFrame(IsoTpConsecutiveFrame consecutiveFrame, byte len)
        {
            ushort remainingBytes;
            if(link.ReceiveSn != consecutiveFrame.SN)
            {
                return IsoTpReturnCode.WRONG_SN;
            }
            remainingBytes = Convert.ToUInt16(link.ReceiveSize - link.ReceiveOffset);
            if(remainingBytes > consecutiveFrame.Data.Length)
            {
                remainingBytes = Convert.ToUInt16(consecutiveFrame.Data.Length);
            }
            if(remainingBytes > len - 1)
            {
                //isotp_user_debug("Consecutive frame too short.");
                return IsoTpReturnCode.LENGTH;
            }
            Array.Copy(consecutiveFrame.Data, 0, link.ReceiveBuffer,link.ReceiveOffset ,remainingBytes);
            link.ReceiveOffset += remainingBytes;
            if(++(link.ReceiveSn) > 0x0F)
            {
                link.ReceiveSn = 0;
            }
            return IsoTpReturnCode.OK;
        }

        public IsoTpReturnCode ReceiveFlowControl(IsoTpFlowControlFrame flowControl, byte len)
        {
            if(len < 3)
            {
                //isotp_user_debug("Flow control frame too short.");
                return IsoTpReturnCode.LENGTH;
            }
            return IsoTpReturnCode.OK;
        }

        public IsoTpReturnCode Send(byte[] payload, ushort size)
        {
            return SendWithId(link.SendArbitrationId, payload, size);
        }

        public IsoTpReturnCode SendWithId(uint id, byte[] payload, ushort size)
        {
            IsoTpReturnCode ret;
            if(link == null)
            {
                //isotp_user_debug("Link is null!");
                return IsoTpReturnCode.ERROR;
            }
            if(size > link.SendBufSize)
            {
                //isotp_user_debug("Message size too large. Increase ISO_TP_MAX_MESSAGE_SIZE to set a larger buffer\n");
                Console.WriteLine($"Attempted to send {size} bytes; max size is {link.SendBufSize}!\n");
                return IsoTpReturnCode.OVERFLOW;
            }
            if(link.SendStatus == IsoTpSendStatus.InProgress)
            {
                //isotp_user_debug("Abort previous message, transmission in progress.\n");
                return IsoTpReturnCode.INPROGRESS;
            }
            link.SendSize = size;
            link.SendOffset = 0;
            Array.Copy(payload, link.SendBuffer, size);
            if(link.SendSize < 8)
            {
                ret = SendSingleFrame(id);
            }
            else
            {
                ret = SendFirstFrame(id);
            }
            if(ret == IsoTpReturnCode.OK)
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

        public void OnCanMessage(byte[] data,byte len)
        {
            IsoTpPciType pciType;
            IsoTpDataArray dataArray;
            IsoTpReturnCode ret;
            if(len < 2 || len > 8)
            {
                return;
            }
            pciType = StructMapping.BytesToStruct<IsoTpPciType>(data);
            dataArray.Elems = new byte[8];
            Array.Copy(data, dataArray.Elems, len);
            switch (pciType.Type)
            {
                case (byte)IsoTpPCIType.SINGLE:
                    IsoTpSingleFrame singleFrame = StructMapping.BytesToStruct<IsoTpSingleFrame>(dataArray.Elems);
                    if(link.ReceiveStatus == IsoTpReceiveStatus.InProgress)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.UNEXP_PDU;
                    }
                    else
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.OK;
                    }
                    ret = ReceiveSingleFrame(singleFrame, len);
                    if(ret == IsoTpReturnCode.OK)
                    {
                        link.ReceiveStatus = IsoTpReceiveStatus.Full;
                    }
                break;
                case (byte)IsoTpPCIType.FIRST_FRAME:
                    IsoTpFirstFrame firstFrame = StructMapping.BytesToStruct<IsoTpFirstFrame>(dataArray.Elems);
                    if(link.ReceiveStatus == IsoTpReceiveStatus.InProgress)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.UNEXP_PDU;
                    }
                    else
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.OK;
                    }
                    ret = ReceiveFirstFrame(firstFrame, len);
                    if(ret == IsoTpReturnCode.OVERFLOW)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.BUFFER_OVFLW;
                        link.ReceiveStatus = IsoTpReceiveStatus.Idle;
                        SendFlowControl(IsoTpPCIFlowStatus.OVERFLOW, 0, 0);
                        break;
                    }
                    if(ret == IsoTpReturnCode.OK)
                    {
                        link.ReceiveStatus = IsoTpReceiveStatus.InProgress;
                        link.ReceiveBsCount = IsoTpConfig.DefaultBlockSize;
                        SendFlowControl(IsoTpPCIFlowStatus.CONTINUE, link.ReceiveBsCount, IsoTpConfig.DefaultStMin);
                        link.ReceiveTimerCr = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                    }
                    break;

                case (byte)IsoTpPCIType.CONSECUTIVE_FRAME:
                    IsoTpConsecutiveFrame consecutiveFrame = StructMapping.BytesToStruct<IsoTpConsecutiveFrame>(dataArray.Elems);
                    if(link.ReceiveStatus != IsoTpReceiveStatus.InProgress)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.UNEXP_PDU;
                        break;
                    }
                    ret = ReceiveConsecutiveFrame(consecutiveFrame, len);
                    if(ret == IsoTpReturnCode.WRONG_SN)
                    {
                        link.ReceiveProtocolResult = IsoTpProtocolResult.WRONG_SN;
                        link.ReceiveStatus = IsoTpReceiveStatus.Idle;
                        break;
                    }
                    if(ret == IsoTpReturnCode.OK)
                    {
                        link.ReceiveTimerCr = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                        if(link.ReceiveOffset >= link.ReceiveSize)
                        {
                            link.ReceiveStatus = IsoTpReceiveStatus.Full;
                        }
                        else
                        {
                            if(--link.ReceiveBsCount == 0)
                            {
                                link.ReceiveBsCount= IsoTpConfig.DefaultBlockSize;
                                SendFlowControl(IsoTpPCIFlowStatus.CONTINUE, link.ReceiveBsCount, IsoTpConfig.DefaultStMin);
                            }
                        }
                    }
                    break;
                case (byte)IsoTpPCIType.FLOW_CONTROL_FRAME:
                    IsoTpFlowControlFrame flowControlFrame = StructMapping.BytesToStruct<IsoTpFlowControlFrame>(dataArray.Elems);
                    if(link.SendStatus != IsoTpSendStatus.InProgress)
                    {
                        break;
                    }
                    ret = ReceiveFlowControl(flowControlFrame, len);
                    if(ret == IsoTpReturnCode.OK)
                    {
                        link.SendTimerBs = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                        if(flowControlFrame.FS == (byte)IsoTpPCIFlowStatus.OVERFLOW)
                        {
                            link.SendProtocolResult = IsoTpProtocolResult.BUFFER_OVFLW;
                            link.SendStatus = IsoTpSendStatus.Error;
                        }
                        else if(flowControlFrame.FS == (byte)IsoTpPCIFlowStatus.WAIT)
                        {
                            link.SendWtfCount += 1;
                            if(link.SendWtfCount > IsoTpConfig.MaxWftNumber)
                            {
                                link.SendProtocolResult = IsoTpProtocolResult.WFT_OVRN;
                                link.SendStatus = IsoTpSendStatus.Error;
                            }
                        }
                        else if(flowControlFrame.FS == (byte)IsoTpPCIFlowStatus.CONTINUE)
                        {
                            if(flowControlFrame.BS == 0)
                            {
                                link.SendBsRemain = InvalidBs;
                            }
                            else
                            {
                                link.SendBsRemain = flowControlFrame.BS;
                            }
                            link.SendStMin = isotp_st_min_to_ms(flowControlFrame.STmin);
                            link.SendWtfCount = 0;
                        }
                    }
                    break;
                default:
                    break;
            }
        }

        public IsoTpReturnCode Receive(byte[] payload,ushort payloadSize,ref ushort outSize)
        {
            ushort copylen;
            if(link.ReceiveStatus == IsoTpReceiveStatus.Full)
            {
                return IsoTpReturnCode.NO_DATA;
            }
            copylen = link.ReceiveSize;
            if(copylen > payloadSize)
            {
                copylen = payloadSize;
            }
            link.ReceiveBuffer = new byte[link.ReceiveSize];
            Array.Copy(link.ReceiveBuffer, payload, copylen);
            outSize = copylen;
            link.ReceiveStatus = IsoTpReceiveStatus.Idle;
            return IsoTpReturnCode.OK;
        }

        public void InitLink(uint sendId, byte[] sendbuf,ushort sendbufSize, byte[] recvbuf,ushort recvbufSize)
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
        }

        public void Poll()
        {
            IsoTpReturnCode ret;
            if(link.SendStatus == IsoTpSendStatus.InProgress)
            {
                if((link.SendBsRemain == InvalidBs || link.SendBsRemain > 0)
                    && (link.SendStMin == 0 || (0 != link.SendStMin && IsoTpTimeAfter(isotp_user_get_ms(), link.SendTimerSt))))
                {
                    ret = SendConsecutiveFrame();
                    if(ret == IsoTpReturnCode.OK)
                    {
                        if(link.SendBsRemain != InvalidBs)
                        {
                            link.SendBsRemain -= 1;
                        }
                        link.SendTimerBs = isotp_user_get_ms() + IsoTpConfig.DefaultResponseTimeout;
                        link.SendTimerSt = isotp_user_get_ms() + link.SendStMin;

                        if(link.SendOffset >= link.SendSize)
                        {
                            link.SendStatus = IsoTpSendStatus.Idle;
                        }
                    }
                    else
                    {
                        link.SendStatus = IsoTpSendStatus.Error;
                    }
                }
                if(IsoTpTimeAfter(isotp_user_get_ms(), link.SendTimerBs))
                {
                    link.SendProtocolResult = IsoTpProtocolResult.TIMEOUT_BS;
                    link.SendStatus = IsoTpSendStatus.Error;
                }
            }
            if(link.ReceiveStatus == IsoTpReceiveStatus.InProgress)
            {
                if (IsoTpTimeAfter(isotp_user_get_ms(), link.ReceiveTimerCr))
                {
                    link.ReceiveProtocolResult = IsoTpProtocolResult.TIMEOUT_CR;
                    link.ReceiveStatus = IsoTpReceiveStatus.Idle;
                }
            }
        }

        private uint isotp_user_get_ms()
        {
            return (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % uint.MaxValue);
        }
        private bool SendCan(uint arbitrationId, byte[] elems)
        {
            Console.WriteLine($"{DateTime.Now.ToString("HH-mm-ss.fff")} CanId:{arbitrationId.ToString("X2")}" +
                $" 发送:{string.Join(" ",elems.Select(b => b.ToString("X2")))}");
            return true;
        }
    }

    public class IsoTpLink
    {
        // Sender parameters
        public uint SendArbitrationId { get; set; } // used to reply consecutive frame
        public byte[] SendBuffer { get; set; } // message buffer
        public ushort SendBufSize { get; set; }
        public ushort SendSize { get; set; }
        public ushort SendOffset { get; set; }
        public byte SendSn { get; set; }
        public ushort SendBsRemain { get; set; } // Remaining block size
        public byte SendStMin { get; set; } // Separation Time between consecutive frames, unit millis
        public byte SendWtfCount { get; set; } // Maximum number of FC.Wait frame transmissions
        public uint SendTimerSt { get; set; } // Last time send consecutive frame
        public uint SendTimerBs { get; set; } // Time until reception of the next FlowControl N_PDU
        public IsoTpProtocolResult SendProtocolResult { get; set; }
        public IsoTpSendStatus SendStatus { get; set; }

        // Receiver parameters
        public uint ReceiveArbitrationId { get; set; }
        public byte[] ReceiveBuffer { get; set; } // message buffer
        public ushort ReceiveBufSize { get; set; }
        public ushort ReceiveSize { get; set; }
        public ushort ReceiveOffset { get; set; }
        public byte ReceiveSn { get; set; }
        public byte ReceiveBsCount { get; set; } // Maximum number of FC.Wait frame transmissions
        public uint ReceiveTimerCr { get; set; } // Time until transmission of the next ConsecutiveFrame N_PDU
        public IsoTpProtocolResult ReceiveProtocolResult { get; set; }
        public IsoTpReceiveStatus ReceiveStatus { get; set; }
    }
}
