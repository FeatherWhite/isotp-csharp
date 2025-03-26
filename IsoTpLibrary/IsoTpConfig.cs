using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsoTpLibrary
{
    public static class IsoTpConfig
    {
        // Max number of messages the receiver can receive at one time
        public const int DefaultBlockSize = 8;

        // The STmin parameter value specifies the minimum time gap allowed between
        // the transmission of consecutive frame network protocol data units
        public const int DefaultStMin = 0;

        // This parameter indicates how many FC N_PDU WTs can be transmitted by the
        // receiver in a row.
        public const int MaxWftNumber = 1;

        // Default timeout to use when waiting for a response during a
        // multi-frame send or receive.
        public const int DefaultResponseTimeout = 100;

        // Determines if by default, padding is added to ISO-TP message frames.
        public const bool FramePadding = true; // Set to true or false based on your requirement
    }
}
