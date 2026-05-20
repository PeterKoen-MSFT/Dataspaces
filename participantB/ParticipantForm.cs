using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public enum ParticipantRole { Provider, Consumer }

public sealed class ParticipantConfig
{
    public required string Title { get; init; }
    public required ParticipantRole Role { get; init; }
    public required string ParticipantName { get; init; }       // e.g. "participant-a"
    public required string ManagementBase { get; init; }        // own controlplane mgmt
    public required string IdentityHubBase { get; init; }       // own IH identity API (port 7081)
    public required string CounterPartyDsp { get; init; }       // peer DSP endpoint
    public required string CounterPartyDid { get; init; }       // peer DID
    public required string DataplanePublicHost { get; init; }   // own dataplane public, host-mapped
    public required string KeycloakBase { get; init; }
    public required string IssuerDid { get; init; }
    public string? AssetsDir { get; init; }                     // host path mounted into resources-a (provider only)
}

public partial class ParticipantForm : Form
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly ParticipantConfig _cfg;

    private TabControl _tabs = null!;
    private RichTextBox _log = null!;
    private Label _status = null!;
    private Label _identity = null!;

    // Catalog tab
    private ListView _catalogList = null!;
    private Button _catalogRefresh = null!;
    private Button _addAssetBtn = null!;       // provider
    private Button _editPolicyBtn = null!;     // provider
    private Button _getCatalogBtn = null!;     // consumer
    private ComboBox _credentialPick = null!;  // consumer
    private Button _negotiateBtn = null!;      // consumer

    // Contracts tab
    private TabPage _contractsTab = null!;
    private ListView _contractsList = null!;
    private Button _contractsRefresh = null!;
    private Button _pullBtn = null!;           // consumer

    // Received tab
    private TabPage _receivedTab = null!;
    private ListView _receivedList = null!;

    // Credentials tab
    private ListView _credList = null!;
    private Button _credRefresh = null!;
    private Button _credRequest = null!;

    private JsonNode? _lastCatalogResponse;
    private string? _adminToken;
    private DateTime _adminTokenExpiry = DateTime.MinValue;

    public static ParticipantForm ForProvider() => new(new ParticipantConfig
    {
        Title = "Participant A — Provider",
        Role = ParticipantRole.Provider,
        ParticipantName = "participant-a",
        ManagementBase = "http://localhost:18081",
        IdentityHubBase = "http://localhost:17081",
        CounterPartyDsp = "http://cp-b:8082/api/dsp/2025-1",
        CounterPartyDid = "did:web:ih-b%3A7083:participant-b",
        DataplanePublicHost = "http://localhost:11102",
        KeycloakBase = "http://localhost:8888",
        IssuerDid = "did:web:issuer%3A10016:issuer",
        AssetsDir = FindAssetsDir(),
    });

    public static ParticipantForm ForConsumer() => new(new ParticipantConfig
    {
        Title = "Participant B — Consumer",
        Role = ParticipantRole.Consumer,
        ParticipantName = "participant-b",
        ManagementBase = "http://localhost:28081",
        IdentityHubBase = "http://localhost:27081",
        CounterPartyDsp = "http://cp-a:8082/api/dsp/2025-1",
        CounterPartyDid = "did:web:ih-a%3A7083:participant-a",
        DataplanePublicHost = "http://localhost:11102",
        KeycloakBase = "http://localhost:8888",
        IssuerDid = "did:web:issuer%3A10016:issuer",
    });

    public ParticipantForm(ParticipantConfig cfg)
    {
        _cfg = cfg;
        Text = cfg.Title;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 750);
        Size = new Size(1300, 850);

        BuildLayout();
        Shown += async (_, _) => await InitialLoadAsync();
    }

    private void BuildLayout()
    {
        SuspendLayout();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180F));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        // Header
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
        _identity = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"{_cfg.ParticipantName}  •  Mgmt {_cfg.ManagementBase}  •  IH {_cfg.IdentityHubBase}",
            Font = new Font(FontFamily.GenericSansSerif, 9F),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _status = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Idle",
            Font = new Font(FontFamily.GenericSansSerif, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var roleLbl = new Label
        {
            Dock = DockStyle.Fill,
            Text = _cfg.Role.ToString(),
            Font = new Font(FontFamily.GenericSansSerif, 18F, FontStyle.Bold),
            ForeColor = _cfg.Role == ParticipantRole.Provider ? Color.DarkGreen : Color.DarkBlue,
            TextAlign = ContentAlignment.MiddleRight,
        };
        header.Controls.Add(_status, 0, 0);
        header.Controls.Add(roleLbl, 1, 0);
        header.SetRowSpan(roleLbl, 2);
        header.Controls.Add(_identity, 0, 1);
        root.Controls.Add(header, 0, 0);

        // Tabs
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildCatalogTab());
        _contractsTab = BuildContractsTab();
        _tabs.TabPages.Add(_contractsTab);
        _receivedTab = BuildReceivedTab();
        _tabs.TabPages.Add(_receivedTab);
        _tabs.TabPages.Add(BuildCredentialsTab());
        root.Controls.Add(_tabs, 0, 1);

        // Activity log
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font(FontFamily.GenericMonospace, 9F),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var logPanel = new GroupBox { Dock = DockStyle.Fill, Text = "Activity log" };
        logPanel.Controls.Add(_log);
        root.Controls.Add(logPanel, 0, 2);

        Controls.Add(root);
        ResumeLayout(true);
    }

    private TabPage BuildCatalogTab()
    {
        var page = new TabPage("Catalog");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = false };

        _catalogRefresh = new Button { Text = "Refresh", Width = 100, Height = 36 };
        _catalogRefresh.Click += async (_, _) => await RefreshCatalogAsync();
        bar.Controls.Add(_catalogRefresh);

        if (_cfg.Role == ParticipantRole.Provider)
        {
            _addAssetBtn = new Button { Text = "Add asset…", Width = 130, Height = 36 };
            _addAssetBtn.Click += async (_, _) => await AddAssetAsync();
            bar.Controls.Add(_addAssetBtn);

            _editPolicyBtn = new Button { Text = "Edit policy…", Width = 130, Height = 36 };
            _editPolicyBtn.Click += async (_, _) => await EditSelectedPolicyAsync();
            bar.Controls.Add(_editPolicyBtn);
        }
        else
        {
            _credentialPick = new ComboBox { Width = 220, Height = 36, DropDownStyle = ComboBoxStyle.DropDownList };
            _getCatalogBtn = new Button { Text = "Get catalog from A", Width = 170, Height = 36 };
            _getCatalogBtn.Click += async (_, _) => await ConsumerGetCatalogAsync();
            bar.Controls.Add(new Label { Text = "Present:", AutoSize = true, Padding = new Padding(8, 10, 4, 0) });
            bar.Controls.Add(_credentialPick);
            bar.Controls.Add(_getCatalogBtn);

            _negotiateBtn = new Button { Text = "Negotiate", Width = 130, Height = 36 };
            _negotiateBtn.Click += async (_, _) => await ConsumerNegotiateAsync();
            bar.Controls.Add(_negotiateBtn);
        }
        layout.Controls.Add(bar, 0, 0);

        _catalogList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
        };
        _catalogList.Columns.Add("Asset ID", 220);
        _catalogList.Columns.Add("URL / Source", 380);
        _catalogList.Columns.Add("Required credential", 200);
        _catalogList.Columns.Add("Contract def", 200);
        layout.Controls.Add(_catalogList, 0, 1);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildCredentialsTab()
    {
        var page = new TabPage("Credentials");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        _credRefresh = new Button { Text = "Refresh", Width = 100, Height = 36 };
        _credRefresh.Click += async (_, _) => await RefreshCredentialsAsync();
        bar.Controls.Add(_credRefresh);

        _credRequest = new Button { Text = "Request credential…", Width = 180, Height = 36 };
        _credRequest.Click += async (_, _) => await RequestCredentialAsync();
        bar.Controls.Add(_credRequest);
        layout.Controls.Add(bar, 0, 0);

        _credList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        _credList.Columns.Add("Type", 200);
        _credList.Columns.Add("State", 110);
        _credList.Columns.Add("Issuer", 280);
        _credList.Columns.Add("Holder request id", 220);
        _credList.Columns.Add("Resource id", 280);
        layout.Controls.Add(_credList, 0, 1);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildContractsTab()
    {
        var page = new TabPage("Contracts");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        _contractsRefresh = new Button { Text = "Refresh", Width = 100, Height = 36 };
        _contractsRefresh.Click += async (_, _) => await RefreshContractsAsync();
        bar.Controls.Add(_contractsRefresh);

        if (_cfg.Role == ParticipantRole.Consumer)
        {
            _pullBtn = new Button { Text = "Execute", Width = 130, Height = 36 };
            _pullBtn.Click += async (_, _) => await ConsumerExecuteAsync();
            bar.Controls.Add(_pullBtn);
        }
        layout.Controls.Add(bar, 0, 0);

        _contractsList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
        };
        _contractsList.Columns.Add("Asset ID", 200);
        _contractsList.Columns.Add("Agreement ID", 320);
        _contractsList.Columns.Add("Counterparty", 280);
        _contractsList.Columns.Add("Signed", 200);
        layout.Controls.Add(_contractsList, 0, 1);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildReceivedTab()
    {
        var page = new TabPage("Received");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _receivedList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
        };
        _receivedList.Columns.Add("Received at", 180);
        _receivedList.Columns.Add("Asset ID", 180);
        _receivedList.Columns.Add("Agreement ID", 320);
        _receivedList.Columns.Add("Bytes", 90);
        _receivedList.Columns.Add("Endpoint", 380);
        layout.Controls.Add(_receivedList, 0, 0);

        page.Controls.Add(layout);
        return page;
    }

    // -------------------- Initial load --------------------

    private async Task InitialLoadAsync()
    {
        try
        {
            await RefreshCatalogAsync();
            await RefreshContractsAsync();
            await RefreshCredentialsAsync();
            await RefreshCredentialPickerAsync();
        }
        catch (Exception ex) { Append($"ERROR initial load: {ex.Message}"); }
    }

    // -------------------- Catalog (provider view = own assets) --------------------

    private async Task RefreshCatalogAsync()
    {
        _status.Text = "Refreshing catalog…";
        _catalogList.Items.Clear();
        try
        {
            if (_cfg.Role == ParticipantRole.Provider)
            {
                var entries = await ListProviderAssetsAsync();
                foreach (var e in entries)
                    _catalogList.Items.Add(new ListViewItem(new[] { e.AssetId, e.Url, e.RequiredCredential, e.ContractDefId }));
                Append($"Provider catalog: {entries.Count} asset(s).");
            }
            else if (_lastCatalogResponse is not null)
            {
                PopulateConsumerCatalogFromResponse(_lastCatalogResponse);
            }
            _status.Text = "Idle";
        }
        catch (Exception ex)
        {
            _status.Text = "Error";
            Append($"ERROR refresh catalog: {ex.Message}");
        }
    }

    private record AssetRow(string AssetId, string Url, string RequiredCredential, string ContractDefId);

    private async Task<List<AssetRow>> ListProviderAssetsAsync()
    {
        var assets = await PostJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/assets/request",
            """{"@context":["https://w3id.org/edc/connector/management/v2"],"@type":"QuerySpec"}""") ?? new JsonArray();
        var contractDefs = await PostJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/contractdefinitions/request",
            """{"@context":["https://w3id.org/edc/connector/management/v2"],"@type":"QuerySpec"}""") ?? new JsonArray();
        var policies = await PostJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/policydefinitions/request",
            """{"@context":["https://w3id.org/edc/connector/management/v2"],"@type":"QuerySpec"}""") ?? new JsonArray();

        var policyById = new Dictionary<string, JsonNode>();
        foreach (var p in policies.AsArray())
        {
            var id = p?["@id"]?.ToString();
            if (!string.IsNullOrEmpty(id)) policyById[id!] = p!;
        }

        var defByAssetId = new Dictionary<string, (string defId, string policyId)>();
        foreach (var def in contractDefs.AsArray())
        {
            if (def is null) continue;
            var defId = def["@id"]?.ToString() ?? "";
            var polId = def["contractPolicyId"]?.ToString() ?? def["accessPolicyId"]?.ToString() ?? "";
            var sel = def["assetsSelector"];
            // selector may be an object or array
            var criteria = sel is JsonArray ja ? ja : (sel is JsonObject jo ? new JsonArray(jo.DeepClone()) : new JsonArray());
            foreach (var c in criteria)
            {
                var operandLeft = c?["operandLeft"]?.ToString() ?? "";
                var operandRight = c?["operandRight"]?.ToString() ?? "";
                if (operandLeft.EndsWith("id") && !string.IsNullOrEmpty(operandRight))
                    defByAssetId[operandRight] = (defId, polId);
            }
        }

        var rows = new List<AssetRow>();
        foreach (var a in assets.AsArray())
        {
            if (a is null) continue;
            var id = a["@id"]?.ToString() ?? "(unknown)";
            var url = a["dataAddress"]?["baseUrl"]?.ToString() ?? a["dataAddress"]?["url"]?.ToString() ?? "";
            var req = "(none)";
            var contractId = "(none)";
            if (defByAssetId.TryGetValue(id, out var d))
            {
                contractId = d.defId;
                if (policyById.TryGetValue(d.policyId, out var pol))
                    req = ExtractRequiredCredential(pol);
            }
            rows.Add(new AssetRow(id, url, req, contractId));
        }
        return rows;
    }

    private static string ExtractRequiredCredential(JsonNode policyDef)
    {
        var permissions = policyDef["policy"]?["permission"];
        var perm = permissions is JsonArray pa ? pa.FirstOrDefault() : permissions;
        var constraint = perm?["constraint"];
        var c = constraint is JsonArray ca ? ca.FirstOrDefault() : constraint;
        var lop = c?["leftOperand"]?.ToString();
        return string.IsNullOrEmpty(lop) ? "(open)" : lop!;
    }

    // -------------------- Provider: add asset --------------------

    private async Task AddAssetAsync()
    {
        if (_addAssetBtn is not null) _addAssetBtn.Enabled = false;
        try
        {
        Append("AddAsset: start");
        if (_cfg.AssetsDir is null || !Directory.Exists(_cfg.AssetsDir))
        {
            Append("AddAsset: AssetsDir missing -> abort");
            MessageBox.Show(this, "Cannot find local assets/ directory bound into resources-a.\nExpected near the repo root.", "Add asset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Append($"AddAsset: AssetsDir = {_cfg.AssetsDir}");
        // Use the legacy Win32 file dialog (AutoUpgradeEnabled=false) to avoid IFileDialog
        // hangs caused by slow shell namespace extensions (OneDrive, quick-access, etc.).
        using var dlg = new OpenFileDialog
        {
            Title = "Pick a file to expose",
            CheckFileExists = true,
            AutoUpgradeEnabled = false,
            RestoreDirectory = true,
            InitialDirectory = _cfg.AssetsDir,
        };
        Append("AddAsset: showing OpenFileDialog");
        // Yield so the activity log repaints before the modal dialog blocks the message loop.
        await Task.Yield();
        var fileResult = dlg.ShowDialog(this);
        Append($"AddAsset: OpenFileDialog -> {fileResult}");
        if (fileResult != DialogResult.OK) return;

        // Ask for credential type
        Append("AddAsset: showing credential picker");
        var requiredCred = PromptCredentialType("Add asset", "Required credential:", "MembershipCredential");
        Append($"AddAsset: credential picker -> {requiredCred ?? "<cancel>"}");
        if (requiredCred is null) return;

        var fileName = Path.GetFileName(dlg.FileName);
        var assetId = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Replace(' ', '-');
        if (string.IsNullOrEmpty(assetId)) assetId = $"asset-{DateTime.Now:HHmmss}";
        Append($"AddAsset: fileName={fileName} assetId={assetId}");

        var dest = Path.Combine(_cfg.AssetsDir, fileName);
        try
        {
            File.Copy(dlg.FileName, dest, overwrite: true);
            Append($"AddAsset: Copied '{fileName}' -> {dest}");
        }
        catch (Exception ex)
        {
            Append($"AddAsset: File.Copy threw: {ex.Message}");
            MessageBox.Show(this, $"Failed to copy file: {ex.Message}", "Add asset", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var policyId = $"require-{requiredCred.ToLowerInvariant()}";
        var contractDefId = $"{assetId}-def";
        _status.Text = "Registering asset…";
        Append($"AddAsset: POST asset {assetId}");
        await TryCreateAssetAsync(assetId, $"http://resources-a:80/{Uri.EscapeDataString(fileName)}");
        Append($"AddAsset: POST policy {policyId}");
        await TryCreatePolicyAsync(policyId, requiredCred);
        Append($"AddAsset: POST contractdef {contractDefId}");
        await TryCreateContractDefAsync(contractDefId, assetId, policyId);
        Append($"AddAsset: published asset '{assetId}' requiring '{requiredCred}'.");
        Append("AddAsset: refreshing catalog");
        await RefreshCatalogAsync();
        _status.Text = "Idle";
        Append("AddAsset: done");
        }
        catch (Exception ex)
        {
            _status.Text = "Error";
            Append($"ERROR add asset: {ex}");
            MessageBox.Show(this, ex.ToString(), "Add asset", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (_addAssetBtn is not null) _addAssetBtn.Enabled = true;
        }
    }

    private async Task TryCreateAssetAsync(string id, string url)
    {
        var body = new JsonObject
        {
            ["@context"] = new JsonArray("https://w3id.org/edc/connector/management/v2"),
            ["@id"] = id,
            ["@type"] = "Asset",
            ["properties"] = new JsonObject { ["description"] = $"Asset {id}" },
            ["dataAddress"] = new JsonObject { ["@type"] = "DataAddress", ["type"] = "HttpData", ["baseUrl"] = url },
        };
        await PostIdempotentAsync($"{_cfg.ManagementBase}/api/mgmt/v4/assets", body.ToJsonString(), $"asset {id}");
    }

    private async Task TryCreatePolicyAsync(string id, string requiredCredential)
    {
        var body = new JsonObject
        {
            ["@context"] = new JsonArray("https://w3id.org/edc/connector/management/v2"),
            ["@type"] = "PolicyDefinition",
            ["@id"] = id,
            ["policy"] = new JsonObject
            {
                ["@type"] = "Set",
                ["permission"] = new JsonArray(new JsonObject
                {
                    ["action"] = "use",
                    ["constraint"] = new JsonObject
                    {
                        ["leftOperand"] = requiredCredential,
                        ["operator"] = "eq",
                        ["rightOperand"] = "active",
                    },
                }),
            },
        };
        await PostIdempotentAsync($"{_cfg.ManagementBase}/api/mgmt/v4/policydefinitions", body.ToJsonString(), $"policy {id}");
    }

    private async Task TryCreateContractDefAsync(string id, string assetId, string policyId)
    {
        var body = new JsonObject
        {
            ["@context"] = new JsonArray("https://w3id.org/edc/connector/management/v2"),
            ["@id"] = id,
            ["@type"] = "ContractDefinition",
            ["accessPolicyId"] = policyId,
            ["contractPolicyId"] = policyId,
            ["assetsSelector"] = new JsonObject
            {
                ["@type"] = "Criterion",
                ["operandLeft"] = "https://w3id.org/edc/v0.0.1/ns/id",
                ["operator"] = "=",
                ["operandRight"] = assetId,
            },
        };
        await PostIdempotentAsync($"{_cfg.ManagementBase}/api/mgmt/v4/contractdefinitions", body.ToJsonString(), $"contract def {id}");
    }

    private async Task EditSelectedPolicyAsync()
    {
        if (_catalogList.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Pick an asset first.", "Edit policy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var row = _catalogList.SelectedItems[0];
        var assetId = row.SubItems[0].Text;
        var current = row.SubItems[2].Text;
        var contractDefId = row.SubItems[3].Text;
        if (contractDefId is "(none)" or "")
        {
            MessageBox.Show(this, "This asset has no contract definition. Re-add it via 'Add asset…'.", "Edit policy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var newCred = PromptCredentialType("Edit policy", $"New required credential for {assetId}:", current is "(none)" or "(open)" ? "MembershipCredential" : current);
        if (newCred is null) return;

        var newPolicyId = $"require-{newCred.ToLowerInvariant()}-{DateTime.Now:HHmmss}";
        try
        {
            await TryCreatePolicyAsync(newPolicyId, newCred);
            // Replace the contract def: delete + recreate (simplest path)
            using (var del = await Http.DeleteAsync($"{_cfg.ManagementBase}/api/mgmt/v4/contractdefinitions/{contractDefId}"))
            {
                Append($"DELETE contractdef {contractDefId} -> {(int)del.StatusCode}");
            }
            await TryCreateContractDefAsync(contractDefId, assetId, newPolicyId);
            Append($"Asset '{assetId}' now requires '{newCred}'.");
            await RefreshCatalogAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Edit policy", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // -------------------- Consumer: get catalog + negotiate --------------------

    private async Task ConsumerGetCatalogAsync()
    {
        _status.Text = "Catalog request…";
        _catalogList.Items.Clear();
        try
        {
            var presented = (_credentialPick.SelectedItem as string) ?? "MembershipCredential";
            Append($"==> Requesting catalog from {_cfg.CounterPartyDsp}");
            Append($"    presenting: {presented}");

            // Note: 'presented' is informational only — EDC 0.17.0's CatalogRequest has no
            // per-request scope override; the controlplane uses its configured DEFAULT scopes.
            var body = new JsonObject
            {
                ["@context"] = new JsonObject { ["@vocab"] = "https://w3id.org/edc/v0.0.1/ns/" },
                ["@type"] = "CatalogRequest",
                ["counterPartyAddress"] = _cfg.CounterPartyDsp,
                ["counterPartyId"] = _cfg.CounterPartyDid,
                ["protocol"] = "dataspace-protocol-http:2025-1",
            };
            var catalog = await PostJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/catalog/request", body.ToJsonString());
            _lastCatalogResponse = catalog;
            if (catalog is not null)
                Append($"<-- catalog raw: {catalog.ToJsonString()}");
            PopulateConsumerCatalogFromResponse(catalog!);
            _status.Text = $"Catalog: {_catalogList.Items.Count} item(s)";
        }
        catch (Exception ex)
        {
            _status.Text = "Error";
            Append($"ERROR catalog: {ex.Message}");
        }
    }

    private void PopulateConsumerCatalogFromResponse(JsonNode catalog)
    {
        _catalogList.Items.Clear();
        // Top-level may be a Catalog object or an array of them.
        var catObjs = new List<JsonObject>();
        if (catalog is JsonArray ca)
        {
            foreach (var c in ca) if (c is JsonObject co) catObjs.Add(co);
        }
        else if (catalog is JsonObject one) catObjs.Add(one);

        foreach (var co in catObjs)
        {
            var datasets = co["dataset"];
            var arr = datasets switch
            {
                JsonArray da => (IEnumerable<JsonNode?>)da,
                JsonObject jo => new[] { (JsonNode?)jo },
                _ => Array.Empty<JsonNode?>()
            };
            foreach (var d in arr)
            {
                if (d is not JsonObject dObj) continue;
                var id = dObj["@id"]?.ToString() ?? "(unknown)";
                var policy = dObj["hasPolicy"];
                var p = policy switch
                {
                    JsonArray pa => pa.FirstOrDefault() as JsonObject,
                    JsonObject po => po,
                    _ => null,
                };
                var permission = p?["permission"];
                var permObj = permission switch
                {
                    JsonArray pma => pma.FirstOrDefault() as JsonObject,
                    JsonObject pmo => pmo,
                    _ => null,
                };
                var constraint = permObj?["constraint"];
                var constraintObj = constraint switch
                {
                    JsonArray ca2 => ca2.FirstOrDefault() as JsonObject,
                    JsonObject co2 => co2,
                    _ => null,
                };
                var lop = constraintObj?["leftOperand"]?.ToString() ?? "(open)";
                var offerId = p?["@id"]?.ToString() ?? "";
                _catalogList.Items.Add(new ListViewItem(new[] { id, "(remote)", lop, offerId }));
            }
        }
        Append($"Catalog populated with {_catalogList.Items.Count} item(s).");
    }

    private async Task ConsumerNegotiateAsync()
    {
        if (_catalogList.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Select an item from the catalog first.\n(Click 'Get catalog from A' if empty.)", "Negotiate", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var row = _catalogList.SelectedItems[0];
        var assetId = row.SubItems[0].Text;
        var offerId = row.SubItems[3].Text;
        if (string.IsNullOrEmpty(offerId)) { MessageBox.Show(this, "No offer id on this row.", "Negotiate"); return; }

        try
        {
            await NegotiateAsync(assetId, offerId);
            await RefreshContractsAsync();
            if (_contractsTab != null) _tabs.SelectedTab = _contractsTab;
        }
        catch (Exception ex)
        {
            _status.Text = "Error";
            Append($"ERROR negotiate: {ex.Message}");
        }
    }

    private async Task NegotiateAsync(string assetId, string offerId)
    {
        var requiredCred = _catalogList.SelectedItems[0].SubItems[2].Text;
        if (requiredCred is "(none)" or "(open)") requiredCred = "MembershipCredential";

        _status.Text = "Negotiating…";
        Append($"==> Negotiating offer {offerId} for asset {assetId} (constraint {requiredCred})");
        var negObj = new JsonObject
        {
            ["@context"] = new JsonArray("https://w3id.org/edc/connector/management/v2"),
            ["@type"] = "ContractRequest",
            ["counterPartyAddress"] = _cfg.CounterPartyDsp,
            ["counterPartyId"] = _cfg.CounterPartyDid,
            ["protocol"] = "dataspace-protocol-http:2025-1",
            ["policy"] = new JsonObject
            {
                ["@type"] = "Offer",
                ["@id"] = offerId,
                ["assigner"] = _cfg.CounterPartyDid,
                ["target"] = assetId,
                ["permission"] = new JsonArray(new JsonObject
                {
                    ["action"] = "use",
                    ["constraint"] = new JsonObject
                    {
                        ["leftOperand"] = requiredCred,
                        ["operator"] = "eq",
                        ["rightOperand"] = "active",
                    },
                }),
                ["prohibition"] = new JsonArray(),
                ["obligation"] = new JsonArray(),
            },
            ["callbackAddresses"] = new JsonArray(),
        };
        var neg = await PostJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/contractnegotiations", negObj.ToJsonString());
        var negId = neg!["@id"]!.ToString();
        Append($"    negotiationId = {negId}");

        string? agreementId = null;
        for (var i = 1; i <= 40; i++)
        {
            var n = await GetJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/contractnegotiations/{negId}");
            var st = n!["state"]?.ToString();
            Append($"    [{i}/40] negotiation state={st}");
            if (st == "FINALIZED") { agreementId = n["contractAgreementId"]?.ToString(); break; }
            if (st == "TERMINATED") throw new InvalidOperationException($"negotiation TERMINATED: {n["errorDetail"]}");
            await Task.Delay(2000);
        }
        if (agreementId is null) throw new InvalidOperationException("negotiation did not finalize");
        Append($"    contractAgreementId = {agreementId}");
        _status.Text = "Negotiated";
    }

    private async Task RefreshContractsAsync()
    {
        if (_contractsList == null) return;
        _contractsList.Items.Clear();
        try
        {
            var query = new JsonObject
            {
                ["@context"] = new JsonObject { ["@vocab"] = "https://w3id.org/edc/v0.0.1/ns/" },
                ["@type"] = "QuerySpec",
            };
            var resp = await PostJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/contractagreements/request", query.ToJsonString());
            JsonArray? arr = resp as JsonArray;
            if (arr == null && resp is JsonObject ro)
            {
                arr = ro["@graph"] as JsonArray;
            }
            if (arr == null) { Append("contracts: empty response"); return; }
            foreach (var node in arr)
            {
                if (node is not JsonObject ag) continue;
                var id = ag["@id"]?.ToString() ?? "";
                var assetId = ag["assetId"]?.ToString() ?? "";
                var providerId = ag["providerId"]?.ToString() ?? "";
                var consumerId = ag["consumerId"]?.ToString() ?? "";
                var counterparty = _cfg.Role == ParticipantRole.Consumer ? providerId : consumerId;
                var signed = "";
                var t = ag["contractSigningDate"];
                if (t != null && long.TryParse(t.ToString(), out var epoch))
                {
                    signed = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                }
                var item = new ListViewItem(new[] { assetId, id, counterparty, signed });
                _contractsList.Items.Add(item);
            }
        }
        catch (Exception ex) { Append($"ERROR refresh contracts: {ex.Message}"); }
    }

    private async Task ConsumerExecuteAsync()
    {
        if (_contractsList.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Select a contract first (Negotiate one from the Catalog tab if empty).", "Execute", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var row = _contractsList.SelectedItems[0];
        var assetId = row.SubItems[0].Text;
        var agreementId = row.SubItems[1].Text;
        if (string.IsNullOrEmpty(agreementId)) { MessageBox.Show(this, "No agreement id on this row.", "Execute"); return; }

        try
        {
            _status.Text = "Initiating transfer…";
            Append($"==> Executing transfer for asset {assetId} (agreement {agreementId})");
            var trObj = new JsonObject
            {
                ["@context"] = new JsonObject { ["@vocab"] = "https://w3id.org/edc/v0.0.1/ns/" },
                ["@type"] = "TransferRequest",
                ["counterPartyAddress"] = _cfg.CounterPartyDsp,
                ["protocol"] = "dataspace-protocol-http:2025-1",
                ["contractId"] = agreementId,
                ["transferType"] = "HttpData-PULL",
            };
            var tr = await PostJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/transferprocesses", trObj.ToJsonString());
            var tpId = tr!["@id"]!.ToString();
            Append($"    transferId = {tpId}");

            for (var i = 1; i <= 40; i++)
            {
                var t = await GetJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v4/transferprocesses/{tpId}");
                var st = t!["state"]?.ToString();
                Append($"    [{i}/40] transfer state={st}");
                if (st is "STARTED" or "COMPLETED") break;
                if (st == "TERMINATED") throw new InvalidOperationException($"transfer TERMINATED: {t["errorDetail"]}");
                await Task.Delay(2000);
            }

            _status.Text = "Fetching EDR…";
            var edr = await GetJsonAsync($"{_cfg.ManagementBase}/api/mgmt/v3/edrs/{tpId}/dataaddress");
            var endpoint = edr!["endpoint"]!.ToString().Replace("http://dp-a:11002", _cfg.DataplanePublicHost);
            var auth = edr["authorization"]!.ToString();
            Append($"    endpoint = {endpoint}");

            _status.Text = "Pulling…";
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", auth);
            var resp = await Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Append("");
            Append($"--- HTTP {(int)resp.StatusCode} from {endpoint} ---");
            Append(body);
            Append("--- END ---");
            _status.Text = $"Pulled {body.Length} bytes";

            if (resp.IsSuccessStatusCode && _receivedList != null)
            {
                var item = new ListViewItem(new[]
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    assetId,
                    agreementId,
                    body.Length.ToString(),
                    endpoint,
                });
                _receivedList.Items.Insert(0, item);
                if (_receivedTab != null) _tabs.SelectedTab = _receivedTab;
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Error";
            Append($"ERROR execute: {ex.Message}");
        }
    }

    // -------------------- Credentials tab --------------------

    private async Task RefreshCredentialsAsync()
    {
        _status.Text = "Loading credentials…";
        _credList.Items.Clear();
        try
        {
            var token = await GetAdminTokenAsync();
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{_cfg.IdentityHubBase}/api/identity/v1alpha/participants/{_cfg.ParticipantName}/credentials");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Append($"Credentials list failed: {(int)resp.StatusCode}: {text}");
                _status.Text = "Idle";
                return;
            }
            var node = JsonNode.Parse(text);
            var arr = node as JsonArray ?? (node?["content"] as JsonArray) ?? new JsonArray();
            foreach (var c in arr)
            {
                if (c is null) continue;
                var id = c["id"]?.ToString() ?? c["@id"]?.ToString() ?? "";
                var rawVc = c["verifiableCredential"]?["rawVc"]?.ToString();
                var stateNum = c["state"]?.ToString() ?? "";
                var state = !string.IsNullOrEmpty(rawVc) ? "ISSUED" : (string.IsNullOrEmpty(stateNum) ? "" : $"state={stateNum}");
                var issuer = c["issuerId"]?.ToString() ?? c["verifiableCredential"]?["credential"]?["issuer"]?["id"]?.ToString() ?? "";
                var holderPid = c["holderPid"]?.ToString() ?? c["metadata"]?["credentialObjectId"]?.ToString() ?? "";
                var types = c["verifiableCredential"]?["credential"]?["type"];
                var typeStr = ExtractCredentialType(types);
                _credList.Items.Add(new ListViewItem(new[] { typeStr, state, issuer, holderPid, id }));
            }
            Append($"Credentials: {_credList.Items.Count} item(s).");
            _status.Text = "Idle";
        }
        catch (Exception ex)
        {
            _status.Text = "Error";
            Append($"ERROR list credentials: {ex.Message}");
        }
    }

    private static string TranslateCredState(JsonNode? s)
    {
        if (s is null) return "";
        var v = s.ToString();
        return v switch
        {
            "0" => "INITIAL",
            "100" => "REQUESTING",
            "200" => "REQUESTED",
            "300" => "ISSUED",
            "400" => "TERMINATED",
            "500" => "EXPIRED",
            _ => v,
        };
    }

    private static string ExtractCredentialType(JsonNode? types)
    {
        if (types is JsonArray ta)
        {
            foreach (var t in ta)
            {
                var s = t?.ToString() ?? "";
                if (s != "VerifiableCredential" && !string.IsNullOrEmpty(s)) return s;
            }
            return ta.Count > 0 ? ta[0]!.ToString() : "";
        }
        return types?.ToString() ?? "";
    }

    private async Task RefreshCredentialPickerAsync()
    {
        if (_cfg.Role != ParticipantRole.Consumer) return;
        var current = _credentialPick.SelectedItem as string;
        var types = new HashSet<string> { "MembershipCredential", "PartnerCredential" };
        foreach (ListViewItem r in _credList.Items)
        {
            var t = r.SubItems[0].Text;
            if (!string.IsNullOrEmpty(t)) types.Add(t);
        }
        _credentialPick.Items.Clear();
        foreach (var t in types.OrderBy(x => x)) _credentialPick.Items.Add(t);
        _credentialPick.SelectedItem = current ?? "MembershipCredential";
        if (_credentialPick.SelectedIndex < 0 && _credentialPick.Items.Count > 0)
            _credentialPick.SelectedIndex = 0;
    }

    private async Task RequestCredentialAsync()
    {
        var type = PromptCredentialType("Request credential", "Credential type to request:", "PartnerCredential");
        if (type is null) return;

        var defId = type switch
        {
            "MembershipCredential" => "membership-credential-def",
            "PartnerCredential" => "partner-credential-def",
            _ => null,
        };
        if (defId is null)
        {
            MessageBox.Show(this, $"No issuer credential definition mapped for '{type}'.", "Request credential", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var token = await GetAdminTokenAsync();
            var holderPid = $"credreq-{_cfg.ParticipantName}-{type}-{DateTime.Now:HHmmss}";
            var body = new JsonObject
            {
                ["issuerDid"] = _cfg.IssuerDid,
                ["holderPid"] = holderPid,
                ["credentials"] = new JsonArray(new JsonObject
                {
                    ["format"] = "VC1_0_JWT",
                    ["type"] = type,
                    ["id"] = defId,
                }),
            };
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{_cfg.IdentityHubBase}/api/identity/v1alpha/participants/{_cfg.ParticipantName}/credentials/request");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            Append($"POST credentials/request -> {(int)resp.StatusCode}: {text}");
            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Conflict)
                throw new HttpRequestException($"request failed: {(int)resp.StatusCode}: {text}");

            // Poll briefly
            for (var i = 1; i <= 10; i++)
            {
                using var poll = new HttpRequestMessage(HttpMethod.Get,
                    $"{_cfg.IdentityHubBase}/api/identity/v1alpha/participants/{_cfg.ParticipantName}/credentials/request/{holderPid}");
                poll.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                using var pr = await Http.SendAsync(poll);
                var pt = await pr.Content.ReadAsStringAsync();
                Append($"  poll [{i}/10] {(int)pr.StatusCode}: {pt}");
                if (pr.IsSuccessStatusCode && pt.Contains("\"ISSUED\"")) break;
                await Task.Delay(2000);
            }
            await RefreshCredentialsAsync();
            await RefreshCredentialPickerAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Request credential", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // -------------------- Helpers --------------------

    private async Task<string> GetAdminTokenAsync()
    {
        if (_adminToken is not null && DateTime.UtcNow < _adminTokenExpiry) return _adminToken;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "admin",
            ["client_secret"] = "edc-v-admin-secret",
            ["scope"] = "identity-api:read identity-api:write issuer-admin-api:write",
        });
        using var resp = await Http.PostAsync($"{_cfg.KeycloakBase}/realms/mvd/protocol/openid-connect/token", form);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Keycloak token failed: {(int)resp.StatusCode}: {text}");
        var node = JsonNode.Parse(text)!;
        _adminToken = node["access_token"]!.ToString();
        var expiresIn = node["expires_in"]?.GetValue<int>() ?? 60;
        _adminTokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(10, expiresIn - 30));
        return _adminToken!;
    }

    private string? PromptCredentialType(string title, string label, string defaultValue)
    {
        using var dlg = new Form
        {
            Text = title,
            Width = 400,
            Height = 180,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var lab = new Label { Left = 12, Top = 12, Width = 360, Text = label };
        var combo = new ComboBox { Left = 12, Top = 40, Width = 360, DropDownStyle = ComboBoxStyle.DropDown };
        combo.Items.Add("MembershipCredential");
        combo.Items.Add("PartnerCredential");
        combo.Text = defaultValue;
        var ok = new Button { Text = "OK", Left = 200, Top = 90, Width = 80, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 290, Top = 90, Width = 80, DialogResult = DialogResult.Cancel };
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        dlg.Controls.AddRange(new Control[] { lab, combo, ok, cancel });
        return dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(combo.Text) ? combo.Text.Trim() : null;
    }

    private static string? FindAssetsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "assets");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "resources.txt")))
                return candidate;
        }
        return null;
    }

    private async Task PostIdempotentAsync(string url, string body, string label)
    {
        using var resp = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) { Append($"OK   {label}"); return; }
        if ((int)resp.StatusCode == 409) { Append($"SKIP {label} (already exists)"); return; }
        throw new HttpRequestException($"POST {url} ({label}) -> {(int)resp.StatusCode}: {text}");
    }

    private static async Task<JsonNode?> PostJsonAsync(string url, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"POST {url} -> {(int)resp.StatusCode}: {text}");
        return JsonNode.Parse(text);
    }

    private static async Task<JsonNode?> GetJsonAsync(string url)
    {
        using var resp = await Http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {url} -> {(int)resp.StatusCode}: {text}");
        return JsonNode.Parse(text);
    }

    private void Append(string line)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke(() => Append(line)); return; }
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }
}
