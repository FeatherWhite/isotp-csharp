using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace IsoTpLibrary
{
    public enum IsoTpReturnCode
    {
        OK = 0,
        ERROR = -1,
        INPROGRESS = -2,
        OVERFLOW = -3,
        WRONG_SN = -4,
        NO_DATA = -5,
        TIMEOUT = -6,
        LENGTH = -7
    }

    // ISOTP sender status
    public enum IsoTpSendStatus
    {
        Idle,               // ISOTP_SEND_STATUS_IDLE
        InProgress,         // ISOTP_SEND_STATUS_INPROGRESS
        Error               // ISOTP_SEND_STATUS_ERROR
    }

    // ISOTP receiver status
    public enum IsoTpReceiveStatus
    {
        Idle,               // ISOTP_RECEIVE_STATUS_IDLE
        InProgress,         // ISOTP_RECEIVE_STATUS_INPROGRESS
        Full                // ISOTP_RECEIVE_STATUS_FULL
    }

    // CAN帧定义
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IsoTpPciType
    {
        [ByteMapping(0, 4)]
        public byte Reserve1; // 4 bits reserved, 4 bits type
        [ByteMapping(4, 4)]
        public byte Type;     // 4 bits type
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
        public byte[] Reserve2; // 7 bytes reserved
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IsoTpSingleFrame
    {
        [ByteMapping(0, 4)]
        public byte SF_DL; // 4 bits SF_DL, 4 bits type
        [ByteMapping(4, 4)]
        public byte Type;  // 4 bits type
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
        public byte[] Data; // 7 bytes of data
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IsoTpFirstFrame
    {
        [ByteMapping(0, 4)]
        public byte FF_DL_high; // 4 bits FF_DL_high, 4 bits type
        [ByteMapping(4, 4)]
        public byte Type;       // 4 bits type
        public byte FF_DL_low;  // 1 byte FF_DL_low
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Data;     // 6 bytes of data
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IsoTpConsecutiveFrame
    {
        [ByteMapping(0, 4)]
        public byte SN;   // 4 bits SN, 4 bits type
        [ByteMapping(4, 4)]
        public byte Type; // 4 bits type
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
        public byte[] Data; // 7 bytes of data
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IsoTpFlowControlFrame
    {
        [ByteMapping(0, 4)]
        public byte FS;       // 4 bits FS, 4 bits type
        [ByteMapping(4, 4)]
        public byte Type;     // 4 bits type
        public byte BS;       // 1 byte BS
        public byte STmin;    // 1 byte STmin
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public byte[] Reserve; // 5 bytes reserved
    }
    public struct IsoTpDataArray
    {
        public byte[] Elems;
    }

    //[StructLayout(LayoutKind.Sequential, Pack = 1)]
    //public struct IsoTpCanMessage
    //{
    //    public IsoTpPciType Common;

    //    public IsoTpSingleFrame SingleFrame;

    //    public IsoTpFirstFrame FirstFrame;

    //    public IsoTpConsecutiveFrame ConsecutiveFrame;

    //    public IsoTpFlowControl FlowControl;

    //    public IsoTpDataArray DataArray;
    //}
    /**************************************************************
 * protocol specific defines
 *************************************************************/

    /* Private: Protocol Control Information (PCI) types, for identifying each frame of an ISO-TP message. */
    public enum IsoTpPCIType : byte
    {
        SINGLE = 0x0,
        FIRST_FRAME = 0x1,
        CONSECUTIVE_FRAME = 0x2,
        FLOW_CONTROL_FRAME = 0x3
    }

    /* Private: Protocol Control Information (PCI) flow control identifiers. */
    public enum IsoTpPCIFlowStatus : byte
    {
        CONTINUE = 0x0,
        WAIT = 0x1,
        OVERFLOW = 0x2
    }
    public enum IsoTpProtocolResult
    {
        OK = 0,
        TIMEOUT_A = -1,
        TIMEOUT_BS = -2,
        TIMEOUT_CR = -3,
        WRONG_SN = -4,
        INVALID_FS = -5,
        UNEXP_PDU = -6,
        WFT_OVRN = -7,
        BUFFER_OVFLW = -8,
        ERROR = -9
    }

}
