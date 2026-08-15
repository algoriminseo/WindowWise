using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using WindowWise.Models;
using WindowWise.ViewModels;

namespace WindowWise.Views
{
    /// <summary>
    /// AudioManagerView.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class AudioManagerView : UserControl
    {
        public AudioManagerView()
        {
            InitializeComponent();
            DataContext = new AudioManagerViewModel();
        }

        private void Preset_Save_New(object sender, RoutedEventArgs e)
        {
            string name = PresetNameInput.Text.Trim();
            if (name.Length == 0) return;
            ((AudioManagerViewModel)DataContext).AudioPreset.SaveNewPreset(name);
            PresetNameInput.Clear();
        }
        private void Preset_Save(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is PresetInfo presetInfo)
            {
                ((AudioManagerViewModel)DataContext).AudioPreset.SavePreset(presetInfo.Id);
            }
        }
        private void Preset_Apply(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is PresetInfo presetInfo)
            {
                ((AudioManagerViewModel)DataContext).AudioPreset.LoadPreset(presetInfo.Id);
            }
        }
    }
}
