using System.Windows;
using WindowWise.Services;

namespace WindowWise.Views
{
    public partial class DefaultDeviceOverlayWindow : Window
    {
        public DefaultDeviceOverlayWindow()
        {
            InitializeComponent();
        }

        public void SetDevice(
            AudioDeviceWrapper device,
            bool useHeadphoneTemplate)
        {
            DataContext = device;

            string templateName = useHeadphoneTemplate
                ? "HeadphoneOverlayTemplate"
                : "SpeakerOverlayTemplate";

            OverlayContent.ContentTemplate =
                (DataTemplate)FindResource(templateName);
        }
    }
}
