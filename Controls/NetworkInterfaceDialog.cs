#if !LIB
using System;
using System.ComponentModel;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.Controls
{
    /// <summary>Selects an individual unicast address from the system's network adapters.</summary>
    public sealed class NetworkInterfaceDialog : Form
    {
        private readonly ListView addresses = new ListView();
        private readonly Button accept = new Button();
        private readonly Label status = new Label();
        private readonly ComponentResourceManager resources =
            new ComponentResourceManager(typeof(NetworkInterfaceDialog));

        public IPAddress SelectedAddress { get; private set; }

        public NetworkInterfaceDialog(IPAddress defaultAddress)
        {
            Text = resources.GetString("Title");
            ClientSize = new Size(760, 360);
            MinimumSize = new Size(560, 280);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;

            addresses.Dock = DockStyle.Fill;
            addresses.View = View.Details;
            addresses.FullRowSelect = true;
            addresses.MultiSelect = false;
            addresses.HideSelection = false;
            addresses.Columns.Add(resources.GetString("Adapter"), 180);
            addresses.Columns.Add(resources.GetString("Address"), 290);
            addresses.Columns.Add(resources.GetString("Status"), 90);
            addresses.Columns.Add(resources.GetString("Description"), 250);
            addresses.SelectedIndexChanged += (s, e) => accept.Enabled = addresses.SelectedItems.Count == 1;

            accept.Text = resources.GetString("OK");
            accept.AutoSize = true;
            accept.Enabled = false;
            accept.Click += (s, e) =>
            {
                if (addresses.SelectedItems.Count != 1) return;
                SelectedAddress = (IPAddress)addresses.SelectedItems[0].Tag;
                DialogResult = DialogResult.OK;
            };
            var cancel = new Button
            {
                Text = resources.GetString("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel
            };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(accept);
            status.Dock = DockStyle.Top;
            status.Height = 48;
            status.Padding = new Padding(8);
            Controls.Add(addresses);
            Controls.Add(status);
            Controls.Add(buttons);
            AcceptButton = accept;
            CancelButton = cancel;

            Load += (s, e) => Populate(defaultAddress);
            ThemeManager.ApplyThemeTo(this);
            Shown += (s, e) =>
            {
                addresses.Focus();
                if (addresses.SelectedItems.Count == 1) addresses.SelectedItems[0].EnsureVisible();
            };
        }

        private void Populate(IPAddress defaultAddress)
        {
            bool incomplete = false;
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                        {
                            var address = unicast.Address;
                            var row = new ListViewItem(new[]
                            {
                                adapter.Name, address.ToString(), adapter.OperationalStatus.ToString(),
                                adapter.Description
                            }) { Tag = address };
                            addresses.Items.Add(row);
                            if (address.Equals(defaultAddress))
                            {
                                row.Selected = true;
                                row.Focused = true;
                            }
                        }
                    }
                    catch (NetworkInformationException) { incomplete = true; }
                }
            }
            catch (NetworkInformationException) { incomplete = true; }
            catch (PlatformNotSupportedException) { incomplete = true; }

            status.Text = resources.GetString(incomplete ? "EnumerationFailed" :
                addresses.Items.Count == 0 ? "NoAddresses" :
                defaultAddress != null && addresses.SelectedItems.Count == 0 ? "DefaultMissing" : "Prompt");
            accept.Enabled = addresses.SelectedItems.Count == 1;
        }

        /// <summary>Shows on the main UI thread. Cancel leaves the supplied address unchanged.</summary>
        public static inputboxreturn ShowPicker(ref IPAddress address)
        {
            var owner = MainV2.instance;
            if (owner == null || owner.IsDisposed || !owner.IsHandleCreated)
                return inputboxreturn.NotSet;

            var initial = address;
            IPAddress selected = null;
            Action show = () =>
            {
                using (var dialog = new NetworkInterfaceDialog(initial))
                    if (dialog.ShowDialog(owner) == DialogResult.OK)
                        selected = dialog.SelectedAddress;
            };
            if (owner.InvokeRequired) owner.Invoke(show);
            else show();

            if (selected == null) return inputboxreturn.Cancel;
            address = selected;
            return inputboxreturn.OK;
        }
    }
}
#endif
