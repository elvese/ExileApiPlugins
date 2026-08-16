using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Merrchant
{
    [DataContract]
    public class PriceRecord
    {
        [DataMember(Name = "key")] public string Key { get; set; }
        [DataMember(Name = "fingerprint")] public string Fingerprint { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "originalPrice")] public int OriginalPrice { get; set; }
        [DataMember(Name = "lastSeenPrice")] public int LastSeenPrice { get; set; }
        [DataMember(Name = "noteStyle")] public string NoteStyle { get; set; }
    }

    [DataContract]
    public class PriceStoreData
    {
        [DataMember(Name = "lastReductionUtc")] public string LastReductionUtc { get; set; }
        [DataMember(Name = "items")] public List<PriceRecord> Items { get; set; }
    }

    public class PriceStore
    {
        private readonly string _path;
        private readonly Dictionary<string, PriceRecord> _byKey = new Dictionary<string, PriceRecord>();
        private readonly Dictionary<string, PriceRecord> _byFingerprint = new Dictionary<string, PriceRecord>();

        public PriceStore(string path)
        {
            _path = path;
        }

        public DateTime? LastReductionUtc { get; set; }

        public int Count => _byKey.Count;

        public void Load()
        {
            _byKey.Clear();
            _byFingerprint.Clear();
            LastReductionUtc = null;

            if (!File.Exists(_path))
                return;

            try
            {
                using (var stream = File.OpenRead(_path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(PriceStoreData));
                    var data = serializer.ReadObject(stream) as PriceStoreData;
                    if (data == null)
                        return;

                    DateTime parsed;
                    if (!string.IsNullOrWhiteSpace(data.LastReductionUtc) &&
                        DateTime.TryParse(data.LastReductionUtc, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out parsed))
                    {
                        LastReductionUtc = parsed.ToUniversalTime();
                    }

                    if (data.Items == null)
                        return;

                    foreach (var item in data.Items)
                    {
                        if (item == null || string.IsNullOrEmpty(item.Key))
                            continue;

                        _byKey[item.Key] = item;
                        if (!string.IsNullOrEmpty(item.Fingerprint))
                            _byFingerprint[item.Fingerprint] = item;
                    }
                }
            }
            catch
            {
                // Corrupt store is ignored and overwritten on the next save.
            }
        }

        public void Save()
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var data = new PriceStoreData
            {
                LastReductionUtc = LastReductionUtc?.ToUniversalTime().ToString("o"),
                Items = _byKey.Values.ToList()
            };

            var tmp = _path + ".tmp";
            using (var stream = File.Create(tmp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, true, true, "  "))
            {
                var serializer = new DataContractJsonSerializer(typeof(PriceStoreData));
                serializer.WriteObject(writer, data);
                writer.Flush();
            }

            if (File.Exists(_path))
                File.Delete(_path);

            File.Move(tmp, _path);
        }

        public PriceRecord Remember(string key, string fingerprint, string name, int currentPrice, string noteStyle)
        {
            PriceRecord record;
            if (!_byKey.TryGetValue(key, out record) &&
                !string.IsNullOrEmpty(fingerprint) &&
                _byFingerprint.TryGetValue(fingerprint, out record))
            {
                _byKey.Remove(record.Key);
                record.Key = key;
                _byKey[key] = record;
            }

            if (record == null)
            {
                record = new PriceRecord
                {
                    Key = key,
                    Fingerprint = fingerprint,
                    Name = name,
                    OriginalPrice = currentPrice,
                    LastSeenPrice = currentPrice,
                    NoteStyle = noteStyle
                };
                _byKey[key] = record;
                if (!string.IsNullOrEmpty(fingerprint))
                    _byFingerprint[fingerprint] = record;
                return record;
            }

            record.Name = name ?? record.Name;
            record.Fingerprint = fingerprint ?? record.Fingerprint;
            record.LastSeenPrice = currentPrice;
            if (!string.IsNullOrEmpty(noteStyle))
                record.NoteStyle = noteStyle;
            if (record.OriginalPrice <= 0)
                record.OriginalPrice = currentPrice;

            if (!string.IsNullOrEmpty(fingerprint))
                _byFingerprint[fingerprint] = record;

            return record;
        }

        public PriceRecord Find(string key, string fingerprint)
        {
            PriceRecord record;
            if (!string.IsNullOrEmpty(key) && _byKey.TryGetValue(key, out record))
                return record;

            if (!string.IsNullOrEmpty(fingerprint) && _byFingerprint.TryGetValue(fingerprint, out record))
                return record;

            return null;
        }

        public void Clear()
        {
            _byKey.Clear();
            _byFingerprint.Clear();
        }
    }
}
