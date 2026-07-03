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
    }
}
