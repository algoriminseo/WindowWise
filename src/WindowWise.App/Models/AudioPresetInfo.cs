using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using WindowWise.Services;
namespace WindowWise.Models
{
    public class PresetInfo
    {
        public int Id { get; set; }
        public string? Name { get; set; }

        public PresetInfo(int id, string? name)
        {
            Id = id;
            Name = name;
        }
    }
    public class AudioPresetInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public ObservableCollection<PresetInfo> Presets { get;} = new();
        private readonly string _connectionString;
        public AudioPresetInfo(AudioPreset audioPreset)
        {
            audioPreset.PresetsChanged += Revoke;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowWise");
            Directory.CreateDirectory(folder);

            string dbPath = Path.Combine(folder, "windowwise.db");
            _connectionString = $"Data Source={dbPath}";

            using var connection = new SqliteConnection(_connectionString);

            connection.Open();

            using var command = connection.CreateCommand();

            command.CommandText =
            """
            SELECT Id, Name FROM AudioPresets;
            """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                int id = reader.GetInt32(0);
                string? name = reader.GetString(1);
                Presets.Add(new PresetInfo(id, name));
            }
            command.Parameters.Clear();
        }
        public void Revoke()
        {
            using var connection = new SqliteConnection(_connectionString);

            connection.Open();

            using var command = connection.CreateCommand();

            command.CommandText =
            """
            SELECT Id, Name FROM AudioPresets;
            """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                int id = reader.GetInt32(0);
                string? name = reader.GetString(1);
                Presets.Add(new PresetInfo(id, name));
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Presets)));
            command.Parameters.Clear();
        }
    }
}
