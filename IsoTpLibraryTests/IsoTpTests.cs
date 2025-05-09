﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        IsoTp isoTp;
        public IsoTpTests()
        {
            byte[] sendbuf = new byte[255];
            byte[] receivebuf = new byte[255];
            isoTp = new IsoTp();
            isoTp.InitLink(0x710, sendbuf, 255, receivebuf, 255);
        }

        [TestMethod()]
        public void TestReceiveEmptyCanMessage()
        {
            Assert.AreEqual(IsoTpReceiveStatus.Idle, isoTp.link.ReceiveStatus);
            IsoTpConsecutiveFrame frame = new IsoTpConsecutiveFrame();
            frame.Data = new byte[7];
            var res = isoTp.ReceiveConsecutiveFrame(frame, 0);
            Assert.AreEqual(IsoTpReceiveStatus.Idle, isoTp.link.ReceiveStatus);
            Assert.AreEqual(IsoTpReturnCode.LENGTH, res);
        }

        [TestMethod()]
        public void TestIsoTpSend()
        {
            byte[] payload = new byte[17]
            {
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 
                0x11
            };
            isoTp.Send(payload,(byte)payload.Length);
            //从ECU中接收到的数据
            isoTp.OnCanMessage(new byte[8] { 0x30, 0x0f, 0x14,0x00, 0x00, 0x00, 0x00, 0x00}, 8);
            while (true)
            {
                isoTp.Poll();
                if(isoTp.link.SendStatus == IsoTpSendStatus.Idle 
                    || isoTp.link.SendStatus == IsoTpSendStatus.Error)
                {
                    break;
                }
            }
        }

        [TestMethod()]
        public void TestIsoTpReceive()
        {
            isoTp.OnCanMessage(new byte[] { 0x10, 0x11, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, 8);
            isoTp.Poll();
            isoTp.OnCanMessage(new byte[] { 0x21, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d },8);
            isoTp.Poll();
            isoTp.OnCanMessage(new byte[] { 0x22, 0x0E, 0x0F, 0x10, 0x11, 0x00, 0x00, 0x00 }, 8);
            isoTp.Poll();
            if(isoTp.link.ReceiveStatus == IsoTpReceiveStatus.Full)
            {
                Console.WriteLine($"时间{DateTime.Now.ToString("HH-mm-ss.fff")}\n接收数据{BitConverter.ToString(isoTp.link.ReceiveBuffer)}");
            }
            isoTp.OnCanMessage(new byte[] { 0x10, 0x11, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, 8);
            isoTp.Poll();
            isoTp.OnCanMessage(new byte[] { 0x21, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d }, 8);
            isoTp.Poll();
            isoTp.OnCanMessage(new byte[] { 0x22, 0x0E, 0x0F, 0x10, 0x11, 0x00, 0x00, 0x00 }, 8);
            isoTp.Poll();
            if (isoTp.link.ReceiveStatus == IsoTpReceiveStatus.Full)
            {
                Console.WriteLine($"时间{DateTime.Now.ToString("HH-mm-ss.fff")}\n接收数据{BitConverter.ToString(isoTp.link.ReceiveBuffer)}");
            }
        }

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
            isoTp.link.SendBuffer = new byte[7] { 1, 2, 3, 4, 5, 6, 7 };
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