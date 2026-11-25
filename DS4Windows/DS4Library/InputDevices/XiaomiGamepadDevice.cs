/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Bluetooth Xiaomi Gamepad (VID 0x2717, PID 0x3144). Buttons, sticks,
    /// analog triggers and both rumble motors are supported. The accelerometer
    /// is left disabled.
    /// </summary>
    public class XiaomiGamepadDevice : DS4Device
    {
        // The pad may stay silent while nothing changes. Mapping still needs a
        // steady tick for macros, mouse movement and timed actions.
        private const uint READ_TIMEOUT_MS = 30;
        private const int RUMBLE_INTERVAL_MS = 20;
        // The Mi button does not reliably send a release, so hold it briefly.
        private const int MI_BUTTON_HOLD_MS = 100;

        private byte[] featureReport;
        private volatile bool inputTransportLost;

        public override event ReportHandler<EventArgs> Report = null;
        public override event EventHandler BatteryChanged;

        public XiaomiGamepadDevice(HidDevice hidDevice, string disName,
            VidPidFeatureSet featureSet = VidPidFeatureSet.DefaultDS4) :
            base(hidDevice, disName, featureSet)
        {
            synced = true;
        }

        public override void PostInit()
        {
            conType = DetermineConnectionType(hDevice);
            Mac = hDevice.GenerateFakeHwSerial();
            deviceType = InputDeviceType.XiaomiGamepad;
            gyroMouseSensSettings = new GyroMouseSens();
            inputReport = new byte[Math.Max(
                (int)hDevice.Capabilities.InputReportByteLength,
                XiaomiGamepadReportParser.InputReportLength)];
            featureReport = new byte[Math.Max(
                (int)hDevice.Capabilities.FeatureReportByteLength, 0)];
            outputReport = Array.Empty<byte>();
            // Replaced by the first input report that carries a battery level.
            battery = 99;
            warnInterval = WARN_INTERVAL_BT;
        }

        public static ConnectionType DetermineConnectionType(HidDevice hidDevice) =>
            ConnectionType.BT;

        // Windows owns the pairing and the address below is generated from the
        // device path, so there is no link this class can drop.
        public override bool DisconnectBT(bool callRemoval = false) => false;

        public override bool DisconnectDongle(bool remove = false) => false;

        public override bool DisconnectWireless(bool callRemoval = false) => false;

        public override bool IsAlive() => synced;

        public override void removeReportHandlers()
        {
            base.removeReportHandlers();
            Report = null;
        }

        public override void StartUpdate()
        {
            inputReportErrorCount = 0;

            if (ds4Input != null)
            {
                Console.WriteLine("Thread already running for Xiaomi Gamepad: " + Mac);
                return;
            }

            ResetAdministrativeInputReadCancellation();
            ResetWorkerStartCommitWitnesses();

            // Feature writes can block, so rumble stays off the input thread.
            // The base class stops and joins ds4Output.
            ds4Output = new Thread(WriteRumble);
            ds4Output.Priority = ThreadPriority.Normal;
            ds4Output.Name = "Xiaomi Gamepad rumble thread: " + Mac;
            ds4Output.IsBackground = true;
            ds4Output.Start();
            MarkOutputWorkerStartCommitted();

            ds4Input = new Thread(ReadInput);
            ds4Input.Priority = ThreadPriority.AboveNormal;
            ds4Input.Name = "Xiaomi Gamepad input thread: " + Mac;
            ds4Input.IsBackground = true;
            ds4Input.Start();
            MarkInputWorkerStartCommitted();
        }

        private void ReadInput()
        {
            unchecked
            {
                Debouncer = SetupDebouncer();
                firstActive = DateTime.UtcNow;
                Queue<double> latencyQueue = new Queue<double>(21);
                double latencySum = 0.0;
                long oldtime = Stopwatch.GetTimestamp();
                long miHoldUntil = 0;
                byte[] latest = new byte[XiaomiGamepadReportParser.InputReportLength];
                XiaomiGamepadReportParser.WriteNeutralInputReport(latest);
                standbySw.Start();

                while (!exitInputThread)
                {
                    if (latencyQueue.Count >= 20)
                    {
                        latencySum -= latencyQueue.Dequeue();
                    }

                    latencySum += lastTimeElapsedDouble;
                    latencyQueue.Enqueue(lastTimeElapsedDouble);
                    Latency = latencySum / latencyQueue.Count;

                    readWaitEv.Set();

                    inputReport[0] = 0;
                    HidDevice.ReadStatus status =
                        hDevice.ReadFile(inputReport, READ_TIMEOUT_MS);

                    readWaitEv.Wait();
                    readWaitEv.Reset();

                    if (exitInputThread) break;

                    if (status != HidDevice.ReadStatus.Success &&
                        status != HidDevice.ReadStatus.WaitTimedOut)
                    {
                        AppLogger.LogToGui($"{Mac} disconnected due to read failure", true);
                        HandleInputTransportLoss();
                        break;
                    }

                    // A report can complete just as the timeout cancels the
                    // read. The cleared first byte reveals that case too.
                    bool newReport =
                        XiaomiGamepadReportParser.IsInputReport(inputReport);
                    long curtime = Stopwatch.GetTimestamp();
                    if (newReport)
                    {
                        Array.Copy(inputReport, latest, latest.Length);
                        if (XiaomiGamepadReportParser.IsMiButtonDown(latest))
                        {
                            miHoldUntil = curtime +
                                Stopwatch.Frequency * MI_BUTTON_HOLD_MS / 1000;
                        }
                    }

                    lastTimeElapsedDouble = (curtime - oldtime) *
                        (1000.0 / Stopwatch.Frequency);
                    lastTimeElapsed = (long)lastTimeElapsedDouble;
                    oldtime = curtime;
                    utcNow = DateTime.UtcNow;

                    // Decode on every tick: later stages may edit cState.
                    XiaomiGamepadReportParser.TryReadControls(latest, cState);
                    cState.PS = curtime < miHoldUntil;

                    cState.PacketCounter = pState.PacketCounter + 1;
                    cState.ReportTimeStamp = utcNow;
                    cState.elapsedTime = lastTimeElapsedDouble * 0.001;
                    cState.totalMicroSec = pState.totalMicroSec +
                        (ulong)(lastTimeElapsedDouble * 1000.0);
                    // DS4 timestamp units are 16/3 microseconds.
                    cState.ds4Timestamp = (ushort)(cState.totalMicroSec * 3 / 16);

                    if (XiaomiGamepadReportParser.TryReadBattery(latest,
                            out int level))
                    {
                        batteryReadingAvailable = true;
                        if (level != battery)
                        {
                            battery = level;
                            BatteryChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }

                    cState.Battery = (byte)battery;

                    if (idleTimeout == 0)
                    {
                        lastActive = utcNow;
                    }
                    else
                    {
                        idleInput = isDS4Idle();
                        if (!idleInput)
                        {
                            lastActive = utcNow;
                        }
                    }

                    if (fireReport)
                    {
                        Report?.Invoke(this, EventArgs.Empty);
                    }

                    cState.CopyTo(pState);

                    if (hasInputEvts)
                    {
                        lock (eventQueueLock)
                        {
                            for (int i = 0, count = eventQueue.Count; i < count; i++)
                            {
                                eventQueue.Dequeue().Invoke();
                            }

                            hasInputEvts = false;
                        }
                    }
                }
            }

            timeoutExecuted = true;
        }

        private void HandleInputTransportLoss()
        {
            inputTransportLost = true;
            exitInputThread = true;
            readWaitEv.Reset();
            isDisconnecting = true;
            StopOutputUpdate();
            RunRemoval();
        }

        private void WriteRumble()
        {
            bool sent = false;
            byte sentSmall = 0, sentBig = 0;

            try
            {
                while (!exitOutputThread)
                {
                    MergeStates();
                    byte small = currentHap.rumbleState.RumbleMotorStrengthRightLightFast;
                    byte big = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
                    currentHap.dirty = false;

                    // Send changes only. A failed write is retried next pass.
                    if ((!sent || small != sentSmall || big != sentBig) &&
                        TrySendRumble(small, big))
                    {
                        sent = true;
                        sentSmall = small;
                        sentBig = big;
                    }

                    Thread.Sleep(RUMBLE_INTERVAL_MS);
                }
            }
            catch (ThreadInterruptedException)
            {
                // Normal stop request.
            }
            finally
            {
                // Never leave the motors running after a stop.
                if (!inputTransportLost)
                {
                    TrySendRumble(0, 0);
                }
            }
        }

        private bool TrySendRumble(byte small, byte big)
        {
            try
            {
                return XiaomiGamepadReportParser.TryWriteRumbleReport(
                        featureReport, small, big) &&
                    hDevice.WriteFeatureReport(featureReport);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or
                ThreadInterruptedException)
            {
                return false;
            }
        }
    }
}
