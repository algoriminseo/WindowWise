using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using WindowWise.Models;

namespace WindowWise.Services
{
    public class AudioPreset
    {
        public event Action? PresetsChanged;
        private readonly string _connectionString;
        private AudioDeviceInfo _audioDeviceInfo;
        public AudioPreset(AudioDeviceInfo adi)
        {
            _audioDeviceInfo = adi;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowWise");

            Directory.CreateDirectory(folder);

            string dbPath = Path.Combine(folder, "windowwise.db");

            _connectionString = $"Data Source={dbPath}";

            using var connection = new SqliteConnection(_connectionString);

            connection.Open();

            using var command = connection.CreateCommand();

            command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS AudioPresets
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                DefaultDevice TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AudioPresetDevices
            (
                PresetId INTEGER NOT NULL,
                DeviceId TEXT NOT NULL,
                DeviceName TEXT NOT NULL,
                Volume REAL NOT NULL,
                PRIMARY KEY (PresetId, DeviceId)
            );
            """;

            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }

        public void LoadPreset(int id)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            // Load the devices and their volumes.
            command.CommandText =
            """
            SELECT DeviceId, DeviceName, Volume FROM AudioPresetDevices WHERE PresetId = $id;
            """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            command.Parameters.Clear();
            while (reader.Read())
            {
                string deviceId = reader.GetString(0);       // 현재 행의 ID
                float volume = (float)reader.GetDouble(2);   // 현재 행의 볼륨

                if (_audioDeviceInfo.Devices.TryGetValue(deviceId, out var device))
                {
                    device.Volume = volume;
                }
            }
            reader.Close();
            command.CommandText =
            """
            SELECT DefaultDevice FROM AudioPresets WHERE Id = $id;
            """;
            command.Parameters.AddWithValue("$id", id);
            var reader2 = command.ExecuteScalar();
            command.Parameters.Clear();
            if (reader2 != null)
            {
                string? defaultDeviceId = reader2.ToString();
                if (defaultDeviceId != null && _audioDeviceInfo.Devices.TryGetValue(defaultDeviceId, out var defaultDevice))
                {
                    _audioDeviceInfo.DefaultDevice = defaultDevice;
                }
            }
        }

        public void SavePreset(int id) { //가정: id는 이미 존재하는 프리셋 아이디
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            foreach (var device in _audioDeviceInfo.Devices.Values)
            {
                command.CommandText =
                """
                INSERT OR REPLACE INTO AudioPresetDevices (PresetId, DeviceId, DeviceName, Volume)
                VALUES ($presetId, $deviceId, $deviceName, $volume);
                """;
                if (_audioDeviceInfo.DefaultDevice != null && device.Id == _audioDeviceInfo.DefaultDevice.Id) {
                    command.CommandText += "UPDATE AudioPresets SET DefaultDevice = $deviceId WHERE Id = $presetId;";
                }
                command.Parameters.AddWithValue("$presetId", id);
                command.Parameters.AddWithValue("$deviceId", device.Id);
                command.Parameters.AddWithValue("$deviceName", device.Name);
                command.Parameters.AddWithValue("$volume", device.Volume);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }
            PresetsChanged?.Invoke();
        }

        public void SaveNewPreset(string name)
        { 
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
            """
            INSERT INTO AudioPresets (Name, DefaultDevice)
            VALUES ($name, $defaultDevice)
            """;
            command.Parameters.AddWithValue("name",name);
            command.Parameters.AddWithValue("defaultDevice", _audioDeviceInfo.DefaultDevice?.Id);
            command.ExecuteNonQuery();

            command.CommandText = "SELECT last_insert_rowid();";
            command.Parameters.Clear();

            int presetId = Convert.ToInt32(command.ExecuteScalar());
            foreach (var device in _audioDeviceInfo.Devices.Values)
            {
                command.CommandText =
                """
                INSERT OR REPLACE INTO AudioPresetDevices (PresetId, DeviceId, DeviceName, Volume)
                VALUES ($presetId, $deviceId, $deviceName, $volume);
                """;
                command.Parameters.AddWithValue("$presetId", presetId);
                command.Parameters.AddWithValue("$deviceId", device.Id);
                command.Parameters.AddWithValue("$deviceName", device.Name);
                command.Parameters.AddWithValue("$volume", device.Volume);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }
            PresetsChanged?.Invoke();
        }
        public void DeletePreset(int id)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
            """
            DELETE FROM AudioPresetDevices WHERE PresetId = $id;
            DELETE FROM AudioPresets WHERE Id = $id;
            """;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            PresetsChanged?.Invoke();
            //삭제 확인 기능 추후 추가
        }
        
    }
}
