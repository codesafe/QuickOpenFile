using System;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace QuickOpenFile.NavigationBar
{
    /// <summary>Navigation Bar 표시 여부. VS 사용자 설정에 저장되어 재시작 후에도 유지된다.</summary>
    internal static class NavigationBarSettings
    {
        private const string Collection = "QuickOpenFile";
        private const string VisibleProperty = "NavigationBarVisible";

        private static bool? s_visible;

        public static event EventHandler VisibilityChanged;

        public static bool IsVisible
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (s_visible == null)
                {
                    try
                    {
                        WritableSettingsStore store = GetStore();
                        s_visible = !store.CollectionExists(Collection) || store.GetBoolean(Collection, VisibleProperty, true);
                    }
                    catch (Exception ex) when (!Microsoft.VisualStudio.ErrorHandler.IsCriticalException(ex))
                    {
                        s_visible = true;
                    }
                }

                return s_visible.Value;
            }

            set
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (s_visible == value)
                {
                    return;
                }

                s_visible = value;
                try
                {
                    WritableSettingsStore store = GetStore();
                    if (!store.CollectionExists(Collection))
                    {
                        store.CreateCollection(Collection);
                    }

                    store.SetBoolean(Collection, VisibleProperty, value);
                }
                catch (Exception ex) when (!Microsoft.VisualStudio.ErrorHandler.IsCriticalException(ex))
                {
                }

                VisibilityChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        private static WritableSettingsStore GetStore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
        }
    }
}
