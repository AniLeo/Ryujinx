﻿using Ryujinx.Rsc.ViewModels;
using Ryujinx.Common;
using Ryujinx.Rsc.Common.Configuration;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Ryujinx.Rsc.ViewModels;


namespace Ryujinx.Ava.Common.Locale
{
    class LocaleManager : BaseModel
    {
        private const string DefaultLanguageCode = "en_US";

        private Dictionary<string, string> _localeStrings;
        private ConcurrentDictionary<string, object[]> _dynamicValues;

        public static LocaleManager Instance { get; } = new LocaleManager();
        public Dictionary<string, string> LocaleStrings { get => _localeStrings; set => _localeStrings = value; }


        public LocaleManager()
        {
            _localeStrings = new Dictionary<string, string>();
            _dynamicValues = new ConcurrentDictionary<string, object[]>();

            Load();
        }

        public void Load()
        {
            string localeLanguageCode = CultureInfo.CurrentCulture.Name.Replace('-', '_');

            // Load english first, if the target language translation is incomplete, we default to english.
            LoadLanguage(DefaultLanguageCode);

            if (localeLanguageCode != DefaultLanguageCode)
            {
                LoadLanguage(localeLanguageCode);
            }
        }

        public string this[string key]
        {
            get
            {
                if (_localeStrings.TryGetValue(key, out string value))
                {
                    if (_dynamicValues.TryGetValue(key, out var dynamicValue))
                    {
                        return string.Format(value, dynamicValue);
                    }

                    return value;
                }

                return key;
            }
            set
            {
                _localeStrings[key] = value;

                OnPropertyChanged();
            }
        }

        public void UpdateDynamicValue(string key, params object[] values)
        {
            _dynamicValues[key] = values;

            OnPropertyChanged("Item");
        }

        public void LoadLanguage(string languageCode)
        {
            string languageJson = EmbeddedResources.ReadAllText($"Ryujinx.Ava/Assets/Locales/{languageCode}.json");

            if (languageJson == null)
            {
                return;
            }

            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(languageJson);

            foreach (var item in strings)
            {
                this[item.Key] = item.Value;
            }
        }
    }
}