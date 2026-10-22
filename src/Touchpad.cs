using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Gestosh
{
    internal sealed class Contact
    {
        public int Id = -1;
        public uint RawX;
        public uint RawY;
        public double X;
        public double Y;
        public bool Tip;
        public bool Confidence = true;
        public override string ToString()
        {
            return string.Format("id={0} tip={1} conf={2} x={3:0.000} y={4:0.000}", Id, Tip ? 1 : 0, Confidence ? 1 : 0, X, Y);
        }
    }

    internal sealed class TouchpadFrame
    {
        public TouchpadDevice Device;
        public int ReportId;
        public int ContactCount = -1;
        public byte[] Raw;
        public bool AddedReportId;
        public double TimeMs;
        public List<Contact> All = new List<Contact>();
        public List<Contact> Active = new List<Contact>();

        public string RawHex { get { return Raw == null ? "" : BitConverter.ToString(Raw); } }

        public bool HasCoordinates()
        {
            return ContactCount > 0 || All.Count > 0;
        }

        public bool IsContactReport
        {
            get
            {
                if (Device == null) return false;
                if (Device.ReportId == 0) return true;
                if (AddedReportId) return true;
                return Raw != null && Raw.Length > 0 && Raw[0] == (byte)Device.ReportId;
            }
        }
    }

    internal sealed class ContactLink
    {
        public int Index;
        public int ReportId;
        public int XMin, XMax, YMin, YMax;
        public bool HasX, HasY, HasId;
        public bool HasTip, HasConfidence;
        public int IdMin, IdMax;
    }

    internal sealed class TouchpadDevice
    {
        public IntPtr RawDevice;
        public string Path = "";
        public IntPtr Preparsed;
        public Native.HIDP_CAPS Caps;
        public int ReportId;
        public int InputReportLength = 30;
        public bool HasContactCount;
        public ushort ContactCountUsage = 0x54;
        public List<ContactLink> Links = new List<ContactLink>();
        public bool? IncludesReportId;
        public long Reports;

        public int MaxContacts { get { return Links.Count; } }

        public bool Open(string path)
        {
            Path = path;
            IntPtr file = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (file == new IntPtr(-1))
            {
                file = Native.CreateFileW(path, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            }
            if (file == new IntPtr(-1))
            {
                Log.Warn("Cannot open touchpad HID device. Win32=" + Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                IntPtr preparsed;
                if (!Native.HidD_GetPreparsedData(file, out preparsed))
                {
                    Log.Warn("HidD_GetPreparsedData failed. Win32=" + Marshal.GetLastWin32Error());
                    return false;
                }
                Preparsed = preparsed;
            }
            finally
            {
                Native.CloseHandle(file);
            }

            var caps = new Native.HIDP_CAPS();
            if (!Native.HidOk(Native.HidP_GetCaps(Preparsed, ref caps)))
            {
                Log.Warn("HidP_GetCaps failed for " + path);
                return false;
            }
            Caps = caps;
            InputReportLength = caps.InputReportByteLength;
            if (InputReportLength <= 0) InputReportLength = 30;

            var links = new Dictionary<int, ContactLink>();
            if (caps.NumberInputValueCaps > 0)
            {
                var valueCaps = new Native.HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
                ushort valueCount = caps.NumberInputValueCaps;
                int status = Native.HidP_GetValueCaps(Native.HIDP_INPUT, valueCaps, ref valueCount, Preparsed);
                if (!Native.HidOk(status))
                {
                    Log.Warn("HidP_GetValueCaps failed: 0x" + status.ToString("x8"));
                    return false;
                }
                for (int i = 0; i < valueCount; i++)
                {
                    var cap = valueCaps[i];
                    ushort usage = cap.IsRange != 0 ? cap.U.UsageMin : cap.U.Usage;
                    if (cap.UsagePage == 0x0D && usage == 0x54 && cap.LinkCollection == 0)
                    {
                        HasContactCount = true;
                        ContactCountUsage = usage;
                        if (ReportId == 0) ReportId = cap.ReportID;
                        continue;
                    }
                    if (cap.LinkCollection == 0) continue;
                    ContactLink link;
                    if (!links.TryGetValue(cap.LinkCollection, out link))
                    {
                        link = new ContactLink();
                        link.Index = cap.LinkCollection;
                        link.ReportId = cap.ReportID;
                        links[cap.LinkCollection] = link;
                    }
                    if (cap.UsagePage == 0x01 && usage == 0x30)
                    {
                        link.HasX = true;
                        link.XMin = cap.LogicalMin;
                        link.XMax = cap.LogicalMax;
                        if (link.ReportId == 0) link.ReportId = cap.ReportID;
                    }
                    else if (cap.UsagePage == 0x01 && usage == 0x31)
                    {
                        link.HasY = true;
                        link.YMin = cap.LogicalMin;
                        link.YMax = cap.LogicalMax;
                    }
                    else if (cap.UsagePage == 0x0D && usage == 0x51)
                    {
                        link.HasId = true;
                        link.IdMin = cap.LogicalMin;
                        link.IdMax = cap.LogicalMax;
                    }
                }
            }

            var nodes = new List<Native.HIDP_LINK_COLLECTION_NODE>();
            if (caps.NumberLinkCollectionNodes > 0)
            {
                var nodeArray = new Native.HIDP_LINK_COLLECTION_NODE[caps.NumberLinkCollectionNodes];
                uint nodeCount = caps.NumberLinkCollectionNodes;
                if (Native.HidOk(Native.HidP_GetLinkCollectionNodes(nodeArray, ref nodeCount, Preparsed)))
                {
                    for (int i = 0; i < nodeCount; i++) nodes.Add(nodeArray[i]);
                }
            }

            foreach (var link in links.Values)
            {
                if (!link.HasX || !link.HasY) continue;
                if (link.ReportId != 0) ReportId = link.ReportId;
                link.HasTip = HasButtonUsage((ushort)link.Index, 0x42);
                link.HasConfidence = HasButtonUsage((ushort)link.Index, 0x47);
                Links.Add(link);
            }
            Links.Sort(delegate(ContactLink a, ContactLink b) { return a.Index.CompareTo(b.Index); });

            if (ReportId == 0)
            {
                foreach (var cap in links.Values) { if (cap.ReportId != 0) { ReportId = cap.ReportId; break; } }
            }
            return Links.Count > 0;
        }

        bool HasButtonUsage(ushort link, ushort usage)
        {
            try
            {
                var caps = new Native.HIDP_BUTTON_CAPS[1];
                ushort count = 1;
                int status = Native.HidP_GetSpecificButtonCaps(Native.HIDP_INPUT, 0x0D, link, usage, caps, ref count, Preparsed);
                return Native.HidOk(status) && count > 0;
            }
            catch (EntryPointNotFoundException)
            {
                // Very old hid.dll; assume the usage exists and let GetUsages decide.
                return usage == 0x42;
            }
            catch (Exception ex)
            {
                Log.Debug("HidP_GetSpecificButtonCaps failed: " + ex.Message);
                return false;
            }
        }

        public TouchpadFrame Parse(byte[] buffer, int offset, int length, double timeMs)
        {
            if (length <= 0 || offset < 0 || offset + length > buffer.Length) return null;
            byte[] raw = new byte[length];
            Array.Copy(buffer, offset, raw, 0, length);

            bool expectedId = ReportId != 0;
            bool startsWithId = expectedId && raw[0] == (byte)ReportId;
            int expectedLength = InputReportLength;
            bool likelyHasId = expectedId && expectedLength > 1 && length == expectedLength;
            bool likelyNoId = expectedId && expectedLength > 1 && length == expectedLength - 1;
            TouchpadFrame withIdFrame = null;
            TouchpadFrame noIdFrame = null;

            bool tryWithIdFirst = !expectedId || (!likelyNoId && (likelyHasId || startsWithId || IncludesReportId == true));
            if (tryWithIdFirst)
            {
                withIdFrame = ParseCore(raw, timeMs, false);
                if (withIdFrame.HasCoordinates())
                {
                    if (startsWithId) IncludesReportId = true;
                    return withIdFrame;
                }
            }

            if (expectedId)
            {
                byte[] withId = new byte[length + 1];
                withId[0] = (byte)ReportId;
                Array.Copy(raw, 0, withId, 1, length);
                noIdFrame = ParseCore(withId, timeMs, true);
                if (noIdFrame.HasCoordinates())
                {
                    IncludesReportId = false;
                    return noIdFrame;
                }
            }

            if (!tryWithIdFirst)
            {
                withIdFrame = ParseCore(raw, timeMs, false);
                if (withIdFrame.HasCoordinates()) return withIdFrame;
            }

            if (withIdFrame != null) return withIdFrame;
            if (noIdFrame != null) return noIdFrame;
            return ParseCore(raw, timeMs, false);
        }

        TouchpadFrame ParseCore(byte[] report, double timeMs, bool addedId)
        {
            var frame = new TouchpadFrame();
            frame.Device = this;
            frame.Raw = report;
            frame.TimeMs = timeMs;
            frame.AddedReportId = addedId;
            frame.ReportId = report.Length > 0 ? report[0] : 0;

            IntPtr p = Marshal.AllocHGlobal(report.Length);
            try
            {
                Marshal.Copy(report, 0, p, report.Length);
                uint count;
                if (HasContactCount && Native.HidOk(Native.HidP_GetUsageValue(Native.HIDP_INPUT, 0x0D, 0, ContactCountUsage,
                        out count, Preparsed, p, (uint)report.Length)))
                {
                    frame.ContactCount = (int)count;
                }

                foreach (var link in Links)
                {
                    uint x, y;
                    if (!Native.HidOk(Native.HidP_GetUsageValue(Native.HIDP_INPUT, 0x01, (ushort)link.Index, 0x30,
                            out x, Preparsed, p, (uint)report.Length))) continue;
                    if (!Native.HidOk(Native.HidP_GetUsageValue(Native.HIDP_INPUT, 0x01, (ushort)link.Index, 0x31,
                            out y, Preparsed, p, (uint)report.Length))) continue;

                    var contact = new Contact();
                    contact.RawX = x;
                    contact.RawY = y;
                    contact.X = Normalize(x, link.XMin, link.XMax);
                    contact.Y = Normalize(y, link.YMin, link.YMax);

                    if (link.HasId)
                    {
                        uint id;
                        if (Native.HidOk(Native.HidP_GetUsageValue(Native.HIDP_INPUT, 0x0D, (ushort)link.Index, 0x51,
                                out id, Preparsed, p, (uint)report.Length)))
                            contact.Id = (int)id;
                    }

                    if (link.HasTip || link.HasConfidence)
                    {
                        var usages = new ushort[64];
                        uint usageCount = (uint)usages.Length;
                        int status = Native.HidP_GetUsages(Native.HIDP_INPUT, 0x0D, (ushort)link.Index, usages,
                            ref usageCount, Preparsed, p, (uint)report.Length);
                        bool tip = false, confidence = true;
                        if (Native.HidOk(status))
                        {
                            if (link.HasTip) tip = Contains(usages, usageCount, 0x42);
                            if (link.HasConfidence) confidence = Contains(usages, usageCount, 0x47);
                        }
                        else if (!link.HasTip)
                        {
                            // Without tip reporting, treat every reported contact in a frame with a count as active.
                            tip = frame.ContactCount > 0;
                        }
                        contact.Tip = link.HasTip ? tip : (frame.ContactCount > 0);
                        contact.Confidence = confidence;
                    }
                    else
                    {
                        contact.Tip = frame.ContactCount > 0;
                        contact.Confidence = true;
                    }

                    frame.All.Add(contact);
                    if (contact.Tip && contact.Confidence) frame.Active.Add(contact);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
            if (frame.ContactCount < 0) frame.ContactCount = frame.All.Count;
            return frame;
        }

        static bool Contains(ushort[] usages, uint count, ushort usage)
        {
            for (uint i = 0; i < count; i++) if (usages[i] == usage) return true;
            return false;
        }

        static double Normalize(uint value, int min, int max)
        {
            if (max <= min) return 0.5;
            double v = ((long)value - min) / (double)((long)max - min);
            return Native.Clamp01(v);
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("touchpad: {0}\r\n", Path);
            sb.AppendFormat("  reportId={0} inputReportLength={1} contactCount={2} contacts={3}\r\n",
                ReportId, InputReportLength, HasContactCount, Links.Count);
            foreach (var link in Links)
            {
                sb.AppendFormat("  link {0}: id={1} x=[{2},{3}] y=[{4},{5}] tip={6} conf={7}\r\n",
                    link.Index, link.HasId, link.XMin, link.XMax, link.YMin, link.YMax, link.HasTip, link.HasConfidence);
            }
            sb.AppendFormat("  includesReportId={0}\r\n", IncludesReportId.HasValue ? (IncludesReportId.Value ? "yes" : "no") : "unknown");
            return sb.ToString();
        }

        public void Dispose()
        {
            if (Preparsed != IntPtr.Zero)
            {
                Native.HidD_FreePreparsedData(Preparsed);
                Preparsed = IntPtr.Zero;
            }
        }
    }

    internal sealed class TouchpadManager : IDisposable
    {
        readonly List<TouchpadDevice> _devices = new List<TouchpadDevice>();
        IntPtr _hwnd;
        bool _disposed;
        public bool Registered;
        public long Reports;
        public long RawReports;
        bool _loggedFirstRaw;
        public event Action<TouchpadFrame> Frame;

        public IList<TouchpadDevice> Devices { get { return _devices; } }

        public bool Discover()
        {
            foreach (var device in _devices) DisposeDevice(device);
            _devices.Clear();

            uint count = 0;
            Native.GetRawInputDeviceList(null, ref count, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICELIST)));
            if (count == 0)
            {
                Log.Info("No raw input devices found.");
                return false;
            }
            var list = new Native.RAWINPUTDEVICELIST[count];
            uint got = Native.GetRawInputDeviceList(list, ref count, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICELIST)));
            for (int i = 0; i < got; i++)
            {
                if (list[i].dwType != Native.RIM_TYPEHID) continue;
                string name = GetDeviceName(list[i].hDevice);
                if (string.IsNullOrEmpty(name)) continue;
                var info = GetDeviceInfo(list[i].hDevice);
                if (info.usUsagePage != 0x0D || info.usUsage != 0x05) continue;

                var device = new TouchpadDevice();
                device.RawDevice = list[i].hDevice;
                if (!device.Open(name))
                {
                    device.Dispose();
                    continue;
                }
                _devices.Add(device);
                Log.Info(device.Describe());
            }
            return _devices.Count > 0;
        }

        public bool Register(IntPtr hwnd)
        {
            _hwnd = hwnd;
            var devices = new Native.RAWINPUTDEVICE[1];
            devices[0].usUsagePage = 0x0D;
            devices[0].usUsage = 0x05;
            devices[0].dwFlags = Native.RIDEV_INPUTSINK;
            devices[0].hwndTarget = hwnd;
            Registered = Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));
            if (!Registered)
                Log.Error("RegisterRawInputDevices failed. Win32=" + Marshal.GetLastWin32Error());
            else
                Log.Info("Raw input registered (precision touchpad).");
            return Registered;
        }

        public void Unregister()
        {
            var devices = new Native.RAWINPUTDEVICE[1];
            devices[0].usUsagePage = 0x0D;
            devices[0].usUsage = 0x05;
            devices[0].dwFlags = Native.RIDEV_REMOVE;
            devices[0].hwndTarget = IntPtr.Zero;
            Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));
            Registered = false;
        }

        public void HandleRawInput(IntPtr lParam)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
            Native.GetRawInputData(lParam, Native.RID_INPUT, null, ref size, headerSize);
            if (size == 0) return;
            byte[] data = new byte[size];
            if (Native.GetRawInputData(lParam, Native.RID_INPUT, data, ref size, headerSize) != size) return;
            if (data.Length < headerSize || BitConverter.ToUInt32(data, 0) != Native.RIM_TYPEHID) return;

            IntPtr deviceHandle = ReadPointer(data, 8);
            TouchpadDevice device = null;
            foreach (var candidate in _devices)
                if (candidate.RawDevice == deviceHandle) { device = candidate; break; }
            if (device == null)
            {
                // The touchpad may have appeared after startup.
                if (Discover())
                {
                    foreach (var candidate in _devices)
                        if (candidate.RawDevice == deviceHandle) { device = candidate; break; }
                }
                if (device == null) return;
            }

            int offset = (int)headerSize;
            if (data.Length < offset + 8) return;
            uint sizeHid = BitConverter.ToUInt32(data, offset);
            uint reportCount = BitConverter.ToUInt32(data, offset + 4);
            int position = offset + 8;
            if (sizeHid == 0 || sizeHid > 4096) return;
            double now = Clock.NowMs;
            for (uint i = 0; i < reportCount; i++)
            {
                if (position + sizeHid > data.Length) break;
                var frame = device.Parse(data, position, (int)sizeHid, now);
                position += (int)sizeHid;
                if (frame == null) continue;
                RawReports++;
                if (!_loggedFirstRaw)
                {
                    _loggedFirstRaw = true;
                    Log.Info("First raw HID report: reportLength=" + (frame.Raw == null ? 0 : frame.Raw.Length) +
                        " raw=" + frame.RawHex);
                }
                if (!frame.IsContactReport) continue;
                Reports++;
                device.Reports++;
                var handler = Frame;
                if (handler != null) handler(frame);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unregister();
            foreach (var device in _devices) DisposeDevice(device);
            _devices.Clear();
        }

        void DisposeDevice(TouchpadDevice device)
        {
            if (device.Preparsed != IntPtr.Zero)
            {
                Native.HidD_FreePreparsedData(device.Preparsed);
                device.Preparsed = IntPtr.Zero;
            }
        }

        static IntPtr ReadPointer(byte[] data, int offset)
        {
            if (IntPtr.Size == 8) return new IntPtr(BitConverter.ToInt64(data, offset));
            return new IntPtr(BitConverter.ToInt32(data, offset));
        }

        static string GetDeviceName(IntPtr handle)
        {
            uint need = 0;
            Native.GetRawInputDeviceInfoW(handle, 0x20000007, IntPtr.Zero, ref need);
            if (need == 0) return "";
            IntPtr buffer = Marshal.AllocHGlobal((int)(need + 2) * 2);
            try
            {
                uint size = need + 2;
                uint result = Native.GetRawInputDeviceInfoW(handle, 0x20000007, buffer, ref size);
                if (result == unchecked((uint)-1) || result == 0) return "";
                return Marshal.PtrToStringUni(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        static Native.RID_DEVICE_INFO_HID GetDeviceInfo(IntPtr handle)
        {
            uint size = (uint)Marshal.SizeOf(typeof(Native.RID_DEVICE_INFO));
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var info = new Native.RID_DEVICE_INFO();
                info.cbSize = size;
                Marshal.StructureToPtr(info, buffer, false);
                Native.GetRawInputDeviceInfoW(handle, 0x2000000b, buffer, ref size);
                info = (Native.RID_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(Native.RID_DEVICE_INFO));
                return info.u.hid;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    internal static class Clock
    {
        static readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();
        public static double NowMs { get { return Watch.Elapsed.TotalMilliseconds; } }
    }
}
