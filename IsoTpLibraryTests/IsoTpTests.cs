using Microsoft.VisualStudio.TestTools.UnitTesting;
using IsoTpLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsoTpLibrary.Tests
{
    [TestClass()]
    public class IsoTpTests
    {
        [TestMethod()]
        public void IsoTpSendFlowControlTest()
        {
            IsoTp isoTp = new IsoTp();
            isoTp.link.SendArbitrationId = 0x1835461D;
            isoTp.link.ReceiveBsCount = 1;
            isoTp.SendFlowControl((byte)IsoTpPCIFlowStatus.CONTINUE, isoTp.link.ReceiveBsCount, IsoTpConfig.DefaultResponseTimeout);
        }
        //[TestMethod()]
        //public void IsoTpSendFlowControlNoPaddingTest()
        //{
        //    IsoTp isoTp = new IsoTp();
        //    isoTp.link.SendArbitrationId = 0x1835461D;
        //    isoTp.link.ReceiveBsCount = 1;
        //    isoTp.FramePadding = false;
        //    isoTp.SendFlowControl((byte)IsoTpPCIFlowStatus.CONTINUE, isoTp.link.ReceiveBsCount, IsoTpConfig.DefaultResponseTimeout);
        //}
        [TestMethod()]
        public void IsoTpSendSingleFrameTest()
        {
            IsoTp isoTp = new IsoTp();
            isoTp.link.SendBuffer = new byte[7] { 1,2,3,4,5,6,7 };
            isoTp.link.SendSize = 7;
            isoTp.SendSingleFrame(0x1835461D);
        }

        [TestMethod()]
        public void IsoTpSendFirstFrameTest()
        {
            IsoTp isoTp = new IsoTp();
            isoTp.link.SendBuffer = new byte[7] { 1, 2, 3, 4, 5, 6, 7 };
            isoTp.link.SendSize = 257;
            isoTp.SendFirstFrame(0x1835461D);
        }
    }
}