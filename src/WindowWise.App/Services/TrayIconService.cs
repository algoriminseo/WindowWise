using System;
using System.Collections.Generic;
using System.Text;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
namespace WindowWise.Services
{
    public sealed class TrayIconService : IDisposable
    {
        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ContextMenuStrip _contextMenu;
        private readonly Drawing.Icon _icon;
        public TrayIconService(Action showWindow, Action exitApplication) {
            _contextMenu = new Forms.ContextMenuStrip();
            var openItem = new Forms.ToolStripMenuItem("Open");
            openItem.Click += (_, _) => showWindow();
            var exitItem = new Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => exitApplication();
            _contextMenu.Items.Add(openItem);
            _contextMenu.Items.Add(exitItem);
            _icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? throw new InvalidOperationException("Application icon can not be loaded");
            _notifyIcon = new Forms.NotifyIcon()
            {
                Icon = _icon,
                Text = "WindowWise",
                ContextMenuStrip = _contextMenu,
                Visible = true
            };
            _notifyIcon.DoubleClick += (_, _) => showWindow();
        }
        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _icon.Dispose();
        }
    }

}
