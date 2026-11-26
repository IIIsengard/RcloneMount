using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using RcloneMount.Services;
using RcloneMount.UI;

namespace RcloneMount
{
    // About tab construction — brand strip, license, third-party credits, privacy
    // placeholder, warranty. Pure UI layout code; extracted from MainForm.cs so
    // future edits to the About tab don't have to scroll through the whole form.
    public partial class MainForm
    {
        // About tab content — brand strip, license note, third-party components,
        // privacy statement, and warranty disclaimer.
        // Layout uses the same section-header + card idiom as the Settings tab.
        private void BuildAboutTab(Panel root)
        {
            // A hidden Dock=Fill panel is still 200 px wide here, and Right anchors are measured
            // from the width at the time a card is added: without this the cards end up ~1950 wide.
            root.Width = LayoutMetrics.TabHostWidth;

            Label MakeSectionHeader(string text, int y) => new()
            {
                Text = text,
                UseMnemonic = false,
                Location = new Point(12, y),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                ForeColor = Theme.TextTertiary,
                BackColor = Color.Transparent
            };

            Label MakeBody(string text, Point location, Size size, bool secondary = false) => new()
            {
                Text = text,
                UseMnemonic = false,
                Location = location,
                Size = size,
                AutoSize = false,
                Font = new Font(Font.FontFamily, 9f),
                ForeColor = secondary ? Theme.TextSecondary : Theme.TextPrimary,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.TopLeft
            };

            LinkLabel MakeLink(string text, string url, Point location) => new()
            {
                Text = text,
                Location = location,
                AutoSize = true,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.AccentHover,
                VisitedLinkColor = Theme.Accent,
                BackColor = Color.Transparent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Tag = url
            };

            void HookLink(LinkLabel lnk) => lnk.LinkClicked += (_, _) => OpenUrl((string)lnk.Tag!);

            // ── Brand card ─────────────────────────────────────────
            var brandCard = MakeSettingsCard("", new Point(12, 12), new Size(LayoutMetrics.CardFullWidth, 108), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var brandIcon = new Panel { Location = new Point(20, 20), Size = new Size(64, 64), BackColor = Color.Transparent };
            brandIcon.Paint += (_, e) =>
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawIcon(AppIcon.Get(), new Rectangle(0, 0, 64, 64));
            };
            var brandName = new Label
            {
                Text = "RcloneMount",
                Location = new Point(100, 22),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent
            };
            var brandVersion = new Label
            {
                Text = $"Version {AppVersion}",
                Location = new Point(100, 54),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 9.5f),
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent
            };
            var brandTagline = new Label
            {
                Text = "A Windows tray manager for rclone mounts.",
                Location = new Point(100, 74),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 9f),
                ForeColor = Theme.TextTertiary,
                BackColor = Color.Transparent
            };
            var lnkSupport = MakeLink("Support this app: Buy me a coffee", "https://buymeacoffee.com/saulfosterv", new Point(420, 54));
            HookLink(lnkSupport);
            brandCard.Controls.AddRange(new Control[] { brandIcon, brandName, brandVersion, brandTagline, lnkSupport });

            // ── License section ────────────────────────────────────
            var hdrLicense = MakeSectionHeader("LICENSE", 132);
            var licenseCard = MakeSettingsCard("", new Point(12, 160), new Size(LayoutMetrics.CardFullWidth, 96), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var lblLicense = MakeBody(
                "RcloneMount is released under the MIT License.\n" +
                "Copyright © " + DateTime.Now.Year + " Saul Foster.",
                new Point(15, 16), new Size(900, 44));
            var lnkLicenseFile = MakeLink("View LICENSE file", "LICENSE.txt", new Point(15, 60));
            // Local file link — resolves to the LICENSE file that ships with the app.
            lnkLicenseFile.LinkClicked += (_, _) => OpenLocalFileOrRepo("LICENSE.txt");
            licenseCard.Controls.Add(lblLicense);
            licenseCard.Controls.Add(lnkLicenseFile);

            // ── Third-party components section ─────────────────────
            var hdrComponents = MakeSectionHeader("THIRD-PARTY COMPONENTS", 268);
            var componentsCard = MakeSettingsCard("", new Point(12, 296), new Size(LayoutMetrics.CardFullWidth, 240), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var lblComponentsIntro = MakeBody(
                "RcloneMount orchestrates two third-party tools that are installed separately on your " +
                "system. It invokes them as external processes; it does not link, bundle, or modify their code.",
                new Point(15, 14), new Size(1040, 40), secondary: true);

            // rclone entry
            var lblRcloneName = new Label
            {
                Text = "rclone",
                Location = new Point(15, 66),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent
            };
            var lblRcloneLic = MakeBody(
                "MIT License. © the rclone authors and contributors.",
                new Point(15, 90), new Size(900, 20), secondary: true);
            var lnkRcloneSite = MakeLink("rclone.org",       "https://rclone.org/",                     new Point(15,  114));
            var lnkRcloneRepo = MakeLink("GitHub",           "https://github.com/rclone/rclone",        new Point(100, 114));
            var lnkRcloneLic  = MakeLink("MIT license text", "https://github.com/rclone/rclone/blob/master/COPYING", new Point(170, 114));
            HookLink(lnkRcloneSite); HookLink(lnkRcloneRepo); HookLink(lnkRcloneLic);

            // WinFsp entry
            var lblWinFspName = new Label
            {
                Text = "WinFsp",
                Location = new Point(15, 148),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                BackColor = Color.Transparent
            };
            var lblWinFspLic = MakeBody(
                "GPL v3 (commercial licence also available). © Bill Zissimopoulos. Installed via the official MSI; not bundled here.",
                new Point(15, 172), new Size(1040, 20), secondary: true);
            var lnkWinFspSite = MakeLink("winfsp.dev",         "https://winfsp.dev/",                              new Point(15,  196));
            var lnkWinFspRepo = MakeLink("GitHub",             "https://github.com/winfsp/winfsp",                  new Point(100, 196));
            var lnkWinFspLic  = MakeLink("GPL v3 license text","https://github.com/winfsp/winfsp/blob/master/License.txt", new Point(170, 196));
            HookLink(lnkWinFspSite); HookLink(lnkWinFspRepo); HookLink(lnkWinFspLic);

            componentsCard.Controls.AddRange(new Control[]
            {
                lblComponentsIntro,
                lblRcloneName, lblRcloneLic, lnkRcloneSite, lnkRcloneRepo, lnkRcloneLic,
                lblWinFspName, lblWinFspLic, lnkWinFspSite, lnkWinFspRepo, lnkWinFspLic
            });

            // ── Privacy section ────────────────────────────────────
            var hdrPrivacy = MakeSectionHeader("PRIVACY", 548);
            var privacyCard = MakeSettingsCard("", new Point(12, 576), new Size(LayoutMetrics.CardFullWidth, 90), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var lblPrivacy = MakeBody(
                "RcloneMount has no account, telemetry or analytics. Settings, logs and transfer totals stay on this PC. " +
                "The app contacts the network only for updates: github.com and api.github.com (RcloneMount, rclone and WinFsp releases). " +
                "It does this at startup if \"Check for Updates\" in Settings is on, when you click Check or Install, and by itself if " +
                "\"Auto-Install Updates\" is on. These are ordinary HTTPS requests, so GitHub sees your IP address and the app name " +
                "(User-Agent). rclone talks only to the cloud providers you set up.",
                new Point(15, 14), new Size(1040, 62), secondary: true);
            privacyCard.Controls.Add(lblPrivacy);

            // ── Support / diagnostics section ──────────────────────
            var hdrSupport = MakeSectionHeader("SUPPORT & DIAGNOSTICS", 684);
            var supportCard = MakeSettingsCard("", new Point(12, 712), new Size(LayoutMetrics.CardFullWidth, 116), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var lblSupport = MakeBody(
                "Export a diagnostic bundle to attach to a bug report. Includes the activity log, " +
                "settings, and version info. Excludes rclone.conf; ExtraFlags values that look like " +
                "credentials (--*password/token/secret/key/auth/bearer) are replaced with [REDACTED].",
                new Point(15, 14), new Size(1040, 42), secondary: true);
            var btnExportDiag = MakeToolbarButton("Export diagnostic bundle…", primary: false);
            btnExportDiag.Size = new Size(210, 30);
            btnExportDiag.Location = new Point(15, 66);
            btnExportDiag.Click += BtnExportDiagnostic_Click;
            var btnOpenLogsFolder = MakeToolbarButton("Open data folder", primary: false);
            btnOpenLogsFolder.Size = new Size(150, 30);
            btnOpenLogsFolder.Location = new Point(235, 66);
            btnOpenLogsFolder.Click += (_, _) =>
            {
                try
                {
                    var folder = System.IO.Path.GetDirectoryName(SettingsService.CurrentSettingsPath);
                    if (!string.IsNullOrEmpty(folder))
                        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                }
                catch (Exception ex) { Notify("Open folder failed", ex.Message, ToolTipIcon.Warning); }
            };
            supportCard.Controls.Add(lblSupport);
            supportCard.Controls.Add(btnExportDiag);
            supportCard.Controls.Add(btnOpenLogsFolder);

            // ── Warranty / disclaimer section ──────────────────────
            var hdrWarranty = MakeSectionHeader("WARRANTY", 840);
            var warrantyCard = MakeSettingsCard("", new Point(12, 868), new Size(LayoutMetrics.CardFullWidth, 82), AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var lblWarranty = MakeBody(
                "This software is provided \"AS IS\", without warranty of any kind, express or implied. " +
                "The authors and contributors are not liable for any claim, damages or other liability " +
                "arising from the use of this software.",
                new Point(15, 14), new Size(1040, 60), secondary: true);
            warrantyCard.Controls.Add(lblWarranty);

            root.Controls.Add(brandCard);
            root.Controls.Add(hdrLicense);
            root.Controls.Add(licenseCard);
            root.Controls.Add(hdrComponents);
            root.Controls.Add(componentsCard);
            root.Controls.Add(hdrPrivacy);
            root.Controls.Add(privacyCard);
            root.Controls.Add(hdrSupport);
            root.Controls.Add(supportCard);
            root.Controls.Add(hdrWarranty);
            root.Controls.Add(warrantyCard);
        }

        private async void BtnExportDiagnostic_Click(object? sender, EventArgs e)
        {
            using var save = new SaveFileDialog
            {
                Title = "Save diagnostic bundle",
                Filter = "Zip archive (*.zip)|*.zip",
                FileName = $"rclonemount-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                SetStatus("Building diagnostic bundle…");
                await DiagnosticBundle.WriteAsync(save.FileName,
                    new DiagnosticBundle.Inputs(AppVersion, rcloneInfo, winFspInfo));
                SetStatus($"Diagnostic bundle saved: {save.FileName}");

                var reveal = MessageBox.Show(
                    $"Saved to:\n{save.FileName}\n\nReveal in Explorer?",
                    "Diagnostic bundle", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (reveal == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{save.FileName}\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not build diagnostic bundle: {ex.Message}",
                    "Diagnostic bundle", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatusError("Diagnostic bundle failed.", ex);
            }
        }

        // Opens a repo-root file with the OS shell if it exists locally; falls back to
        // the app directory. Used by About-tab links so LICENSE / NOTICES open in the
        // user's default text viewer.
        private void OpenLocalFileOrRepo(string relativeName)
        {
            try
            {
                var candidates = new[]
                {
                    System.IO.Path.Combine(AppContext.BaseDirectory, relativeName),
                    System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativeName),
                };
                foreach (var path in candidates)
                {
                    if (System.IO.File.Exists(path))
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        return;
                    }
                }
            }
            catch { }
        }
    }
}
