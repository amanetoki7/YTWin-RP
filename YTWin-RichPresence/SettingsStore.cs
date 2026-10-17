using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace YTWin_RichPresence.Properties {

    // Keep user settings in one fixed file instead of .NET's default user.config, whose folder depends on the
    // exe's location and version, so moving or updating the app used to start over with default settings.
    [SettingsProvider(typeof(SettingsStore))]
    public sealed partial class Settings { }
}

namespace YTWin_RichPresence {

    /// <summary>
    /// Stores user settings as JSON in %LOCALAPPDATA%\YTWin-RichPresence\settings.json. Writes go through a
    /// temporary file and keep the previous version as a backup, which is read if the main file is damaged.
    /// On first run the newest legacy user.config is imported.
    /// </summary>
    internal sealed class SettingsStore : SettingsProvider, IApplicationSettingsProvider {
        private const string SettingsSection = "YTWin_RichPresence.Properties.Settings";
        private static readonly object fileLock = new();

        public static string SettingsFile => Path.Combine(Constants.AppDataFolder, "settings.json");
        private static string BackupFile => SettingsFile + ".bak";

        public override string ApplicationName { get => Constants.ApplicationStylisedName; set { } }
        public override string Name => nameof(SettingsStore);

        public override void Initialize(string? name, NameValueCollection? config) {
            base.Initialize(name ?? Name, config);
        }

        public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection collection) {
            var stored = Load();
            var values = new SettingsPropertyValueCollection();
            foreach (SettingsProperty property in collection) {
                var value = new SettingsPropertyValue(property) { IsDirty = false };
                if (stored.TryGetValue(property.Name, out var serialized)) {
                    value.SerializedValue = serialized;
                }
                values.Add(value);
            }
            return values;
        }

        public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection collection) {
            lock (fileLock) {
                var stored = Load();
                foreach (SettingsPropertyValue value in collection) {
                    stored[value.Name] = value.SerializedValue as string ?? value.PropertyValue?.ToString() ?? "";
                }
                Save(stored);
            }
        }

        public SettingsPropertyValue GetPreviousVersion(SettingsContext context, SettingsProperty property) {
            var value = new SettingsPropertyValue(property);
            if (Load().TryGetValue(property.Name, out var serialized)) {
                value.SerializedValue = serialized;
            }
            return value;
        }

        public void Reset(SettingsContext context) {
            lock (fileLock) {
                Save(new Dictionary<string, string>());
            }
        }

        public void Upgrade(SettingsContext context, SettingsPropertyCollection properties) { }

        // ------------------------------------------------------------------

        private static Dictionary<string, string> Load() {
            lock (fileLock) {
                var stored = TryRead(SettingsFile) ?? TryRead(BackupFile);
                if (stored != null) {
                    return stored;
                }
                if (File.Exists(SettingsFile) || File.Exists(BackupFile)) {
                    return new Dictionary<string, string>(); // both damaged: defaults, and the next save replaces them
                }
                var imported = ImportLegacyUserConfig();
                if (imported.Count > 0) {
                    Save(imported);
                }
                return imported;
            }
        }

        private static Dictionary<string, string>? TryRead(string path) {
            try {
                if (!File.Exists(path)) {
                    return null;
                }
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            } catch {
                return null;
            }
        }

        private static void Save(Dictionary<string, string> stored) {
            Directory.CreateDirectory(Constants.AppDataFolder);
            var temp = SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(SettingsFile)) {
                File.Replace(temp, SettingsFile, BackupFile, ignoreMetadataErrors: true);
            } else {
                File.Move(temp, SettingsFile);
            }
        }

        /// <summary>Settings saved by earlier versions under %LOCALAPPDATA%\YTWin-RichPresence\&lt;exe&gt;_&lt;hash&gt;\&lt;version&gt;\user.config.</summary>
        private static Dictionary<string, string> ImportLegacyUserConfig() {
            var result = new Dictionary<string, string>();
            try {
                if (!Directory.Exists(Constants.AppDataFolder)) {
                    return result;
                }
                var newest = Directory.EnumerateFiles(Constants.AppDataFolder, "user.config", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest == null) {
                    return result;
                }
                var section = XDocument.Load(newest).Root?.Element("userSettings")?.Element(SettingsSection);
                foreach (var setting in section?.Elements("setting") ?? Enumerable.Empty<XElement>()) {
                    var name = setting.Attribute("name")?.Value;
                    if (name != null) {
                        result[name] = setting.Element("value")?.Value ?? "";
                    }
                }
                new Logger().Log($"Imported {result.Count} settings from {newest}");
            } catch (Exception ex) {
                new Logger().Log($"Could not import old settings: {ex.Message}");
            }
            return result;
        }
    }
}
