using System;
using System.Security;
using Microsoft.Win32;

namespace Gestosh
{
    /// <summary>
    /// Temporarily disables the built-in Windows three-/four-finger slides while
    /// Gestosh runs, because Windows would otherwise also switch apps / show the
    /// desktop for the same contacts.  Original values are stored in
    /// HKCU\Software\Gestosh and restored on exit or on demand.
    /// </summary>
    internal sealed class SystemGestures
    {
        const string TouchpadKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
        const string MarkerKey = @"Software\Gestosh";

        readonly Config _config;
        readonly GestureValue _three = new GestureValue("ThreeFingerSlideEnabled", "");
        readonly GestureValue _four = new GestureValue("FourFingerSlideEnabled", "Four");

        public SystemGestures(Config config)
        {
            _config = config;
        }

        public bool Managed { get { return _three.Managed || _four.Managed; } }
        public bool Wanted { get { return _config.ManageThreeFingerGesture || _config.ManageFourFingerGesture; } }

        public void TakeOver()
        {
            _three.TakeOver(_config.ManageThreeFingerGesture, "three-finger");
            _four.TakeOver(_config.ManageFourFingerGesture, "four-finger");
        }

        public void Restore()
        {
            _three.Restore();
            _four.Restore();
        }

        public void RestoreNow()
        {
            _three.RestoreNow();
            _four.RestoreNow();
        }

        sealed class GestureValue
        {
            readonly string _valueName;
            readonly string _prefix;
            bool _managed;
            bool _originalExists;
            int _originalValue = 1;

            public GestureValue(string valueName, string prefix)
            {
                _valueName = valueName;
                _prefix = prefix;
            }

            public bool Managed { get { return _managed; } }

            string ManagedName { get { return _prefix + "Managed"; } }
            string ExistsName { get { return _prefix + "OriginalExists"; } }
            string OriginalName { get { return _prefix + "OriginalValue"; } }

            public void TakeOver(bool wanted, string label)
            {
                if (!wanted) return;
                RegistryKey marker = null;
                RegistryKey key = null;
                try
                {
                    try { marker = Registry.CurrentUser.CreateSubKey(MarkerKey); }
                    catch (Exception ex) { Log.Warn("Cannot create the gesture marker key (crash recovery will be unavailable): " + ex.Message); }

                    key = Registry.CurrentUser.OpenSubKey(TouchpadKey, true);
                    if (key == null)
                    {
                        Log.Warn("The PrecisionTouchPad registry key is not available; cannot take over the Windows " + label + " gesture.");
                        return;
                    }

                    bool wasManaged = marker != null && Convert.ToInt32(marker.GetValue(ManagedName, 0)) == 1;
                    object currentObject = key.GetValue(_valueName);
                    int current = currentObject == null ? 1 : Convert.ToInt32(currentObject);

                    if (wasManaged)
                    {
                        _originalExists = Convert.ToInt32(marker.GetValue(ExistsName, 1)) != 0;
                        _originalValue = Convert.ToInt32(marker.GetValue(OriginalName, 1));
                        if (current != 0)
                        {
                            Log.Info("Windows " + label + " setting was changed externally; no longer managing it.");
                            ClearMarker(marker);
                            _managed = false;
                            return;
                        }
                        _managed = true;
                        key.SetValue(_valueName, 0, RegistryValueKind.DWord);
                        Log.Info("Windows " + label + " slide remains disabled (managed by Gestosh).");
                        return;
                    }

                    if (current == 0)
                    {
                        Log.Info("Windows " + label + " slide is already disabled.");
                        return;
                    }

                    _originalExists = currentObject != null;
                    _originalValue = current;
                    if (marker != null)
                    {
                        marker.SetValue(ManagedName, 1, RegistryValueKind.DWord);
                        marker.SetValue(ExistsName, _originalExists ? 1 : 0, RegistryValueKind.DWord);
                        marker.SetValue(OriginalName, _originalValue, RegistryValueKind.DWord);
                    }
                    key.SetValue(_valueName, 0, RegistryValueKind.DWord);
                    _managed = true;
                    Log.Info("Temporarily disabled the built-in " + label + " slide to avoid conflicts.");
                }
                catch (SecurityException ex) { Log.Warn("Cannot change " + label + " setting: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Log.Warn("Cannot change " + label + " setting: " + ex.Message); }
                catch (Exception ex) { Log.Warn("Cannot change " + label + " setting: " + ex.Message); }
                finally
                {
                    if (key != null) key.Dispose();
                    if (marker != null) marker.Dispose();
                }
            }

            public void Restore()
            {
                if (!_managed) return;
                try
                {
                    using (RegistryKey marker = TryOpenMarker())
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(TouchpadKey, true))
                    {
                        if (key != null)
                        {
                            object currentObject = key.GetValue(_valueName);
                            int current = currentObject == null ? 1 : Convert.ToInt32(currentObject);
                            if (current == 0)
                            {
                                if (_originalExists) key.SetValue(_valueName, _originalValue, RegistryValueKind.DWord);
                                else key.DeleteValue(_valueName, false);
                            }
                        }
                        ClearMarker(marker);
                    }
                    Log.Info("Restored the built-in " + Label + " setting.");
                }
                catch (Exception ex) { Log.Warn("Cannot restore " + Label + " setting: " + ex.Message); }
                finally { _managed = false; }
            }

            public void RestoreNow()
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(TouchpadKey, true))
                    using (RegistryKey marker = TryOpenMarker())
                    {
                        int value = _originalValue;
                        bool exists = _originalExists;
                        if (marker != null && Convert.ToInt32(marker.GetValue(ManagedName, 0)) == 1)
                        {
                            exists = Convert.ToInt32(marker.GetValue(ExistsName, 1)) != 0;
                            value = Convert.ToInt32(marker.GetValue(OriginalName, 1));
                        }
                        if (key != null)
                        {
                            if (exists) key.SetValue(_valueName, value, RegistryValueKind.DWord);
                            else key.DeleteValue(_valueName, false);
                        }
                        ClearMarker(marker);
                    }
                    _managed = false;
                    Log.Info("Windows " + Label + " slide restored by user request.");
                }
                catch (Exception ex) { Log.Warn("Cannot restore " + Label + " setting: " + ex.Message); }
            }

            string Label
            {
                get { return _valueName.StartsWith("Four", StringComparison.Ordinal) ? "four-finger" : "three-finger"; }
            }

            RegistryKey TryOpenMarker()
            {
                try { return Registry.CurrentUser.OpenSubKey(MarkerKey, true); }
                catch { return null; }
            }

            void ClearMarker(RegistryKey marker)
            {
                if (marker == null) return;
                try
                {
                    marker.DeleteValue(ManagedName, false);
                    marker.DeleteValue(ExistsName, false);
                    marker.DeleteValue(OriginalName, false);
                }
                catch { }
            }
        }
    }
}
