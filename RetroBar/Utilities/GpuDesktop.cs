using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The picture of the desktop behind a window, taken on the graphics card: DXGI desktop duplication hands over each frame of the
    /// screen as the compositor presents it (no reading of the screen pixel by pixel), the part of it behind a blur is cut out, and the
    /// card makes it smaller (the mip levels of a texture are a box filter) - only that small picture is read back, a few kilobytes.
    /// Windows excluded from capture (the bars) are not in the frame, as in any capture.
    ///
    /// One thread for each display does all of it, and is the only one that touches that display's device (a Direct3D context is not for
    /// two threads): it waits for the frames, and for each blur whose place changed in a frame (or that asked for another place) it makes
    /// the small picture and tells the blur, which only has to take it. Anything that goes wrong only makes the picture not come, and the
    /// blur reads the screen the way it always did.
    /// </summary>
    internal static class GpuDesktop
    {
        internal class Output
        {
            public string Name;
            public int Left, Top, Right, Bottom;
            public ID3D11Device Device;
            public ID3D11DeviceContext Context;
            public IDXGIOutput1 Source;
            public IDXGIOutputDuplication Duplication;
            public ID3D11Texture2D Desktop;
            public bool HasFrame;
            public long Frame;
            // What changed in each frame (where), so that a blur whose place did not is not read again.
            public readonly List<(long Frame, RawRect[] Rects)> Events = new();
            public volatile bool NeedsReset;
        }

        internal sealed class Request
        {
            public int Left, Top, Right, Bottom, Shift;
            public int Key => HashCode.Combine(Left, Top, Right, Bottom, Shift);
        }

        /// <summary>What a blur has: where it wants its picture from, and the newest picture made for it.</summary>
        public sealed class Subscription : IDisposable
        {
            internal Action Changed;
            internal volatile Request Wanted;
            internal volatile bool Disposed;

            // Of the thread of the display (made and thrown away there).
            internal Output Home;
            internal long GrabbedFrame = -1;
            internal int GrabbedKey;
            internal ID3D11Texture2D Region;
            internal ID3D11ShaderResourceView RegionView;
            internal ID3D11Texture2D Staging;
            internal int RegionWidth = -1, RegionHeight = -1, RegionShift = -1;

            private readonly object _gate = new();
            private byte[] _pixels;
            private int _width, _height, _key;
            private long _version, _taken = -1;

            /// <summary>Where the picture is to be from (pixels of the virtual screen), made smaller 2^shift times along each side.</summary>
            public void Want(int left, int top, int right, int bottom, int shift)
            {
                var wanted = Wanted;
                if (wanted == null || wanted.Left != left || wanted.Top != top || wanted.Right != right || wanted.Bottom != bottom || wanted.Shift != shift)
                {
                    Wanted = new Request { Left = left, Top = top, Right = right, Bottom = bottom, Shift = shift };
                }
            }

            /// <summary>
            /// The newest picture, if it is of the place that is wanted now: 32 bit pixels (blue, green, red, whatever); unchanged when it is the
            /// one that was taken last. False when there is none (yet).
            /// </summary>
            public bool TryTake(out byte[] pixels, out int width, out int height, out bool unchanged)
            {
                pixels = null;
                width = height = 0;
                unchanged = false;
                var wanted = Wanted;
                lock (_gate)
                {
                    if (_pixels == null || wanted == null || _key != wanted.Key)
                    {
                        return false;
                    }

                    pixels = _pixels;
                    width = _width;
                    height = _height;
                    unchanged = _version == _taken;
                    _taken = _version;
                    return true;
                }
            }

            internal void Put(byte[] pixels, int width, int height, int key)
            {
                lock (_gate)
                {
                    _pixels = pixels;
                    _width = width;
                    _height = height;
                    _key = key;
                    _version++;
                }
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private static readonly object Gate = new();
        private static readonly List<Subscription> Subscriptions = new();
        private static List<Output> _outputs;
        private static bool _unavailable;

        private const int WaitTimeout = unchecked((int)0x887A0027);

        /// <summary>A blur asks for pictures through this. Null when this cannot be had at all; the blur then reads the screen itself.</summary>
        public static Subscription Subscribe(Action changed)
        {
            if (_unavailable)
            {
                return null;
            }

            lock (Gate)
            {
                try
                {
                    _outputs ??= Enumerate();
                    if (_outputs.Count == 0)
                    {
                        _unavailable = true;
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    ManagedShell.Common.Logging.ShellLogger.Info("GpuDesktop: " + ex.Message);
                    _unavailable = true;
                    return null;
                }

                var subscription = new Subscription { Changed = changed };
                Subscriptions.Add(subscription);
                return subscription;
            }
        }

        private static List<Output> Enumerate()
        {
            var list = new List<Output>();
            if (DXGI.CreateDXGIFactory1(out IDXGIFactory1 factory).Failure)
            {
                return list;
            }

            for (int a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                for (int o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    // (A device and context for each display, though two may be of one card: a context is for one thread.)
                    if (D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context).Failure)
                    {
                        break;
                    }

                    var desc = output.Description;
                    list.Add(new Output
                    {
                        Name = desc.DeviceName,
                        Left = desc.DesktopCoordinates.Left,
                        Top = desc.DesktopCoordinates.Top,
                        Right = desc.DesktopCoordinates.Right,
                        Bottom = desc.DesktopCoordinates.Bottom,
                        Device = device,
                        Context = context,
                        Source = output.QueryInterface<IDXGIOutput1>(),
                    });
                }
            }

            foreach (Output started in list)
            {
                Output output = started;
                new Thread(() => Run(output)) { IsBackground = true, Name = "GpuDesktop " + output.Name, Priority = ThreadPriority.AboveNormal }.Start();
            }

            return list;
        }

        // ---- the thread of a display
        private static void Run(Output output)
        {
            while (true)
            {
                try
                {
                    if (output.Duplication == null || output.NeedsReset)
                    {
                        Drop(output);
                        output.NeedsReset = false;
                        Thread.Sleep(300);
                        try
                        {
                            output.Duplication = output.Source.DuplicateOutput(output.Device);
                        }
                        catch (Exception)
                        {
                            Thread.Sleep(1500);
                            continue;
                        }
                    }

                    var result = output.Duplication.AcquireNextFrame(50, out OutduplFrameInfo info, out IDXGIResource resource);
                    if (result.Failure)
                    {
                        if (result.Code != WaitTimeout)
                        {
                            // The display changed under it (or the secure desktop came): made again.
                            output.NeedsReset = true;
                            continue;
                        }
                    }
                    else
                    {
                        try
                        {
                            if (info.LastPresentTime != 0)
                            {
                                using ID3D11Texture2D frame = resource.QueryInterface<ID3D11Texture2D>();
                                if (output.Desktop == null)
                                {
                                    var description = frame.Description;
                                    description.BindFlags = BindFlags.None;
                                    description.CPUAccessFlags = CpuAccessFlags.None;
                                    description.MiscFlags = ResourceOptionFlags.None;
                                    description.Usage = ResourceUsage.Default;
                                    output.Desktop = output.Device.CreateTexture2D(description);
                                }

                                lock (output.Device)
                                {
                                    output.Context.CopyResource(output.Desktop, frame);
                                }

                                output.HasFrame = true;
                                output.Frame++;
                                output.Events.Add((output.Frame, ChangedRects(output, info, frame.Description.Width, frame.Description.Height)));
                                if (output.Events.Count > 240)
                                {
                                    output.Events.RemoveRange(0, 120);
                                }
                            }
                        }
                        finally
                        {
                            resource.Dispose();
                            output.Duplication.ReleaseFrame();
                        }
                    }

                    if (output.HasFrame)
                    {
                        Subscription[] subscriptions;
                        lock (Gate)
                        {
                            subscriptions = Subscriptions.ToArray();
                        }

                        foreach (Subscription subscription in subscriptions)
                        {
                            Serve(output, subscription);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ManagedShell.Common.Logging.ShellLogger.Info("GpuDesktop worker: " + ex.Message);
                    output.NeedsReset = true;
                    Thread.Sleep(1000);
                }
            }
        }

        // The picture for one blur, if it is of this display and there is something new for it: another place than the last time, or a change
        // in the frames of the screen inside it since.
        private static void Serve(Output output, Subscription subscription)
        {
            if (subscription.Disposed)
            {
                if (subscription.Home == output)
                {
                    lock (output.Device)
                    {
                        Release(subscription);
                    }

                    lock (Gate)
                    {
                        Subscriptions.Remove(subscription);
                    }
                }
                else if (subscription.Home == null)
                {
                    lock (Gate)
                    {
                        Subscriptions.Remove(subscription);
                    }
                }

                return;
            }

            Request wanted = subscription.Wanted;
            if (wanted == null || wanted.Right <= wanted.Left || wanted.Bottom <= wanted.Top)
            {
                return;
            }

            int centerX = (wanted.Left + wanted.Right) / 2, centerY = (wanted.Top + wanted.Bottom) / 2;
            if (centerX < output.Left || centerX >= output.Right || centerY < output.Top || centerY >= output.Bottom ||
                wanted.Left < output.Left || wanted.Top < output.Top || wanted.Right > output.Right || wanted.Bottom > output.Bottom)
            {
                return;
            }

            int key = wanted.Key;
            int left = wanted.Left - output.Left, top = wanted.Top - output.Top, right = wanted.Right - output.Left, bottom = wanted.Bottom - output.Top;
            bool same = subscription.Home == output && subscription.GrabbedKey == key;
            if (same && (subscription.GrabbedFrame == output.Frame || !Affected(output, subscription.GrabbedFrame, left, top, right, bottom)))
            {
                subscription.GrabbedFrame = output.Frame;
                return;
            }

            int width = right - left, height = bottom - top, shift = wanted.Shift;
            if (width < 1 << shift || height < 1 << shift)
            {
                return;
            }

            byte[] pixels;
            int smallWidth = Math.Max(1, width >> shift), smallHeight = Math.Max(1, height >> shift);
            lock (output.Device)
            {
                if (subscription.Home != output)
                {
                    Release(subscription);
                    subscription.Home = output;
                }

                Ensure(output, subscription, width, height, shift);
                output.Context.CopySubresourceRegion(subscription.Region, 0, 0, 0, 0, output.Desktop, 0, new Box(left, top, 0, right, bottom, 1));
                if (shift > 0)
                {
                    output.Context.GenerateMips(subscription.RegionView);
                }

                output.Context.CopySubresourceRegion(subscription.Staging, 0, 0, 0, 0, subscription.Region, shift, null);
                MappedSubresource map = output.Context.Map(subscription.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                pixels = new byte[smallWidth * smallHeight * 4];
                try
                {
                    for (int y = 0; y < smallHeight; y++)
                    {
                        Marshal.Copy(map.DataPointer + y * map.RowPitch, pixels, y * smallWidth * 4, smallWidth * 4);
                    }
                }
                finally
                {
                    output.Context.Unmap(subscription.Staging, 0);
                }
            }

            subscription.GrabbedFrame = output.Frame;
            subscription.GrabbedKey = key;
            subscription.Put(pixels, smallWidth, smallHeight, key);
            subscription.Changed?.Invoke();
        }

        private static void Release(Subscription subscription)
        {
            subscription.Region?.Dispose();
            subscription.Region = null;
            subscription.RegionView?.Dispose();
            subscription.RegionView = null;
            subscription.Staging?.Dispose();
            subscription.Staging = null;
            subscription.RegionWidth = subscription.RegionHeight = subscription.RegionShift = -1;
            subscription.Home = null;
            subscription.GrabbedFrame = -1;
        }

        private static void Drop(Output output)
        {
            output.Duplication?.Dispose();
            output.Duplication = null;
            output.Desktop?.Dispose();
            output.Desktop = null;
            output.HasFrame = false;
            output.Events.Clear();
            output.Frame++;
            Subscription[] subscriptions;
            lock (Gate)
            {
                subscriptions = Subscriptions.ToArray();
            }

            lock (output.Device)
            {
                foreach (Subscription subscription in subscriptions)
                {
                    if (subscription.Home == output)
                    {
                        Release(subscription);
                    }
                }
            }
        }

        // Where on the screen a frame is not what the one before it was (the rectangles it says it has drawn or moved), or all of it when it
        // does not say.
        private static RawRect[] ChangedRects(Output output, OutduplFrameInfo info, int width, int height)
        {
            var all = new[] { new RawRect(0, 0, width, height) };
            if (info.TotalMetadataBufferSize == 0)
            {
                return all;
            }

            try
            {
                var dirty = new RawRect[128];
                if (output.Duplication.GetFrameDirtyRects(dirty.Length * 16, dirty, out int dirtyBytes).Failure || dirtyBytes > dirty.Length * 16)
                {
                    return all;
                }

                var moves = new OutduplMoveRect[64];
                if (output.Duplication.GetFrameMoveRects(moves.Length * 24, moves, out int moveBytes).Failure || moveBytes > moves.Length * 24)
                {
                    return all;
                }

                int dirtyCount = dirtyBytes / 16, moveCount = moveBytes / 24;
                var rects = new RawRect[dirtyCount + moveCount * 2];
                Array.Copy(dirty, rects, dirtyCount);
                for (int i = 0; i < moveCount; i++)
                {
                    // Both the place a rectangle was moved to and the one it came from (that one is drawn over by what was under it).
                    RawRect to = moves[i].DestinationRect;
                    rects[dirtyCount + i * 2] = to;
                    rects[dirtyCount + i * 2 + 1] = new RawRect(moves[i].SourcePoint.X, moves[i].SourcePoint.Y, moves[i].SourcePoint.X + (to.Right - to.Left), moves[i].SourcePoint.Y + (to.Bottom - to.Top));
                }

                return rects;
            }
            catch (Exception)
            {
                return all;
            }
        }

        // Did something change inside the box (relative to the output) in the frames after the one given?
        private static bool Affected(Output output, long after, int left, int top, int right, int bottom)
        {
            if (output.Events.Count == 0 || output.Events[0].Frame > after + 1)
            {
                return true;
            }

            foreach (var (frame, rects) in output.Events)
            {
                if (frame <= after)
                {
                    continue;
                }

                foreach (RawRect r in rects)
                {
                    if (r.Left < right && r.Right > left && r.Top < bottom && r.Bottom > top)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // The cut-out region (with the mip levels it is made small with) and the small picture read back from it: kept while the size is.
        private static void Ensure(Output output, Subscription subscription, int width, int height, int shift)
        {
            if (subscription.Region != null && subscription.RegionWidth == width && subscription.RegionHeight == height && subscription.RegionShift == shift)
            {
                return;
            }

            subscription.Region?.Dispose();
            subscription.RegionView?.Dispose();
            subscription.Staging?.Dispose();

            subscription.Region = output.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = shift + 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            subscription.RegionView = output.Device.CreateShaderResourceView(subscription.Region);
            subscription.Staging = output.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = Math.Max(1, width >> shift),
                Height = Math.Max(1, height >> shift),
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
            subscription.RegionWidth = width;
            subscription.RegionHeight = height;
            subscription.RegionShift = shift;
        }
    }
}
