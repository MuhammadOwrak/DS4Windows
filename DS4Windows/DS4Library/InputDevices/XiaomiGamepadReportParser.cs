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

// The Xiaomi Gamepad report layout comes from irungentoo/Xiaomi_gamepad
// (https://github.com/irungentoo/Xiaomi_gamepad), MIT License,
// Copyright (c) 2016 irungentoo.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Decodes the Xiaomi Gamepad HID reports:
    /// [0] report ID 0x04, [1..2] buttons, [4] d-pad hat, [5..8] sticks,
    /// [11..12] analog triggers, [13..18] accelerometer (not used),
    /// [19] battery level, [20] Mi button.
    /// </summary>
    internal static class XiaomiGamepadReportParser
    {
        internal const byte InputReportId = 0x04;
        internal const int InputReportLength = 21;
        internal const byte RumbleReportId = 0x20;
        internal const int RumbleReportLength = 3;

        private const byte NeutralHat = 0x0F;
        private const byte StickCenter = 0x80;

        internal static bool IsInputReport(ReadOnlySpan<byte> report) =>
            report.Length >= InputReportLength && report[0] == InputReportId;

        internal static void WriteNeutralInputReport(Span<byte> report)
        {
            report.Clear();
            report[0] = InputReportId;
            report[4] = NeutralHat;
            report[5] = report[6] = report[7] = report[8] = StickCenter;
        }

        /// <summary>
        /// Writes every control except the Mi (PS) button, which needs timing
        /// owned by the device.
        /// </summary>
        internal static bool TryReadControls(ReadOnlySpan<byte> report,
            DS4State state)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (!IsInputReport(report)) return false;

            byte first = report[1], second = report[2];
            state.Cross = (first & 0x01) != 0;      // A
            state.Circle = (first & 0x02) != 0;     // B
            state.Square = (first & 0x08) != 0;     // X
            state.Triangle = (first & 0x10) != 0;   // Y
            state.L1 = (first & 0x40) != 0;
            state.R1 = (first & 0x80) != 0;
            state.Share = (second & 0x04) != 0;     // Back
            state.Options = (second & 0x08) != 0;   // Start
            state.L3 = (second & 0x20) != 0;
            state.R3 = (second & 0x40) != 0;

            // Hat: 0 = up, then clockwise to 7 = up-left. 15 = released.
            byte hat = report[4];
            state.DpadUp = hat is 7 or 0 or 1;
            state.DpadRight = hat is 1 or 2 or 3;
            state.DpadDown = hat is 3 or 4 or 5;
            state.DpadLeft = hat is 5 or 6 or 7;

            // Same orientation as DS4State: 0 = left/up, 255 = right/down.
            state.LX = report[5];
            state.LY = report[6];
            state.RX = report[7];
            state.RY = report[8];

            state.L2 = state.L2Raw = report[11];
            state.R2 = state.R2Raw = report[12];
            state.L2Btn = state.L2 > 0;
            state.R2Btn = state.R2 > 0;
            return true;
        }

        internal static bool IsMiButtonDown(ReadOnlySpan<byte> report) =>
            IsInputReport(report) && (report[20] & 0x01) != 0;

        internal static bool TryReadBattery(ReadOnlySpan<byte> report,
            out int percent)
        {
            percent = IsInputReport(report) ? report[19] : 0;
            return percent is >= 1 and <= 100;
        }

        /// <summary>
        /// Report ID, small (fast) motor, big (slow) motor. The destination may
        /// be longer than three bytes when Windows requires padding.
        /// </summary>
        internal static bool TryWriteRumbleReport(Span<byte> report,
            byte smallMotor, byte bigMotor)
        {
            if (report.Length < RumbleReportLength) return false;

            report.Clear();
            report[0] = RumbleReportId;
            report[1] = smallMotor;
            report[2] = bigMotor;
            return true;
        }
    }
}
