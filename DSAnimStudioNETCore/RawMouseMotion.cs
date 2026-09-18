using System;
using System.Collections.Generic;
using System.Drawing;
using Linearstar.Windows.RawInput.Native;

namespace DSAnimStudio
{
    // WM_INPUT may contain desktop positions (RDP/tablet/streaming), not mouse counts.
    // Convert those positions at the input boundary so camera and timeline consumers
    // always receive displacement exactly once, independently of Update/Draw cadence.
    internal sealed class RawMouseMotion
    {
        private struct AbsolutePosition
        {
            public int X, Y;
            public Rectangle Desktop;
            public long RemainderX, RemainderY;
        }

        private readonly Dictionary<IntPtr, AbsolutePosition> positions = new();
        public bool IsAbsolute { get; private set; }

        public void ResetAbsolutePositions() => positions.Clear();

        public Point Translate(IntPtr device, RawMouseFlags flags, int x, int y, Rectangle desktop)
        {
            bool absolute = (flags & RawMouseFlags.MoveAbsolute) != 0;
            if (!absolute)
            {
                // Some devices send separate button-only packets with flags == None.
                // Such packets must not switch an absolute drag into cursor-warp mode.
                if (x != 0 || y != 0)
                {
                    IsAbsolute = false;
                    positions.Remove(device);
                }
                return new Point(x, y);
            }

            IsAbsolute = true;
            if (x < 0 || x > 65535 || y < 0 || y > 65535 || desktop.Width <= 0 || desktop.Height <= 0)
            {
                positions.Remove(device);
                return Point.Empty;
            }

            bool initialized = positions.TryGetValue(device, out var previous)
                && previous.Desktop == desktop && (flags & RawMouseFlags.AttributesChanged) == 0;
            if (!initialized)
            {
                // Bound hotplug/device metadata. Never interpret the first position as movement.
                if (positions.Count >= 16) positions.Clear();
                positions[device] = new AbsolutePosition { X = x, Y = y, Desktop = desktop };
                return Point.Empty;
            }

            long dx = ((long)x - previous.X) * desktop.Width + previous.RemainderX;
            long dy = ((long)y - previous.Y) * desktop.Height + previous.RemainderY;
            // Carry subpixels as exact integer fractions: even a whole-screen trip split
            // into many packets must not lose its last pixel to floating point rounding.
            int pixelX = (int)(dx / 65535), pixelY = (int)(dy / 65535);
            positions[device] = new AbsolutePosition
            {
                X = x, Y = y, Desktop = desktop,
                RemainderX = dx % 65535, RemainderY = dy % 65535,
            };
            return new Point(pixelX, pixelY);
        }
    }
}
