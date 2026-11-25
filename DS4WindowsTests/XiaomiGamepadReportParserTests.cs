using System;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class XiaomiGamepadReportParserTests
{
    private static byte[] Neutral()
    {
        var report = new byte[XiaomiGamepadReportParser.InputReportLength];
        XiaomiGamepadReportParser.WriteNeutralInputReport(report);
        return report;
    }

    private static DS4State Read(byte[] report)
    {
        var state = new DS4State();
        Assert.IsTrue(XiaomiGamepadReportParser.TryReadControls(report, state));
        return state;
    }

    [TestMethod]
    public void NeutralReportPressesNothing()
    {
        DS4State state = Read(Neutral());
        Assert.IsFalse(state.Cross || state.Circle || state.Square || state.Triangle);
        Assert.IsFalse(state.DpadUp || state.DpadRight || state.DpadDown || state.DpadLeft);
        Assert.AreEqual((byte)128, state.LX);
        Assert.AreEqual((byte)128, state.RY);
        Assert.AreEqual((byte)0, state.L2);
    }

    [TestMethod]
    public void ButtonsMapToTheirDs4Positions()
    {
        byte[] report = Neutral();
        report[1] = 0x01 | 0x08 | 0x40;
        report[2] = 0x04 | 0x20;
        DS4State state = Read(report);
        Assert.IsTrue(state.Cross && state.Square && state.L1 && state.Share && state.L3);
        Assert.IsFalse(state.Circle || state.Triangle || state.R1 || state.Options || state.R3);

        report[1] = 0x02 | 0x10 | 0x80;
        report[2] = 0x08 | 0x40;
        state = Read(report);
        Assert.IsTrue(state.Circle && state.Triangle && state.R1 && state.Options && state.R3);
        Assert.IsFalse(state.Cross || state.Square || state.L1 || state.Share || state.L3);
    }

    [TestMethod]
    [DataRow(0, true, false, false, false)]
    [DataRow(1, true, true, false, false)]
    [DataRow(2, false, true, false, false)]
    [DataRow(3, false, true, true, false)]
    [DataRow(4, false, false, true, false)]
    [DataRow(5, false, false, true, true)]
    [DataRow(6, false, false, false, true)]
    [DataRow(7, true, false, false, true)]
    [DataRow(15, false, false, false, false)]
    public void HatDecodesClockwise(int hat, bool up, bool right, bool down, bool left)
    {
        byte[] report = Neutral();
        report[4] = (byte)hat;
        DS4State state = Read(report);
        Assert.AreEqual(up, state.DpadUp);
        Assert.AreEqual(right, state.DpadRight);
        Assert.AreEqual(down, state.DpadDown);
        Assert.AreEqual(left, state.DpadLeft);
    }

    [TestMethod]
    public void SticksTriggersBatteryAndMiButtonUseTheirOffsets()
    {
        byte[] report = Neutral();
        report[5] = 1; report[6] = 2; report[7] = 3; report[8] = 4;
        report[11] = 200; report[12] = 100;
        report[19] = 75; report[20] = 1;
        DS4State state = Read(report);
        Assert.AreEqual((byte)1, state.LX);
        Assert.AreEqual((byte)2, state.LY);
        Assert.AreEqual((byte)3, state.RX);
        Assert.AreEqual((byte)4, state.RY);
        Assert.AreEqual((byte)200, state.L2);
        Assert.AreEqual((byte)100, state.R2);
        Assert.IsTrue(state.L2Btn && state.R2Btn);
        Assert.IsTrue(XiaomiGamepadReportParser.TryReadBattery(report, out int battery));
        Assert.AreEqual(75, battery);
        Assert.IsTrue(XiaomiGamepadReportParser.IsMiButtonDown(report));
    }

    [TestMethod]
    public void ShortOrForeignReportsAreRejected()
    {
        var state = new DS4State();
        Assert.IsFalse(XiaomiGamepadReportParser.TryReadControls(new byte[20], state));
        byte[] report = Neutral();
        report[0] = 0x01;
        Assert.IsFalse(XiaomiGamepadReportParser.TryReadControls(report, state));
    }

    [TestMethod]
    public void RumbleReportPutsSmallMotorFirstAndClearsPadding()
    {
        byte[] report = { 9, 9, 9, 9 };
        Assert.IsTrue(XiaomiGamepadReportParser.TryWriteRumbleReport(report, 10, 20));
        CollectionAssert.AreEqual(new byte[] { 0x20, 10, 20, 0 }, report);
        Assert.IsFalse(XiaomiGamepadReportParser.TryWriteRumbleReport(new byte[2], 1, 1));
    }

    [TestMethod]
    public void DeviceUsesTheSupportedWorkerLifecycle()
    {
        Assert.AreEqual(DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                typeof(XiaomiGamepadDevice), hasHidInterface: true));
        // Saved profiles store these numbers, so the new type is appended.
        Assert.AreEqual(11u, (uint)InputDeviceType.XiaomiGamepad);
    }
}
