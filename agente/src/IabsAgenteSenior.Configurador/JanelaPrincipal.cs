using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using System.Text.Json;
using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Comandos;
using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Diagnostico;
using IabsAgenteSenior.Servico;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Configurador;

/// <summary>
/// Janela de configuração do integrador: conexão com a IABS, banco do Senior e
/// situação do serviço. Usa as mesmas regras do "configurar" da linha de
/// comando (ServicoConfiguracao): testa o banco e a IABS antes de gravar, cifra
/// os segredos e protege a pasta de dados. Senha e chave gravadas nunca aparecem.
/// </summary>
public sealed class JanelaPrincipal : Form
{
    private static readonly Color CorDestaque = Color.FromArgb(31, 58, 95);
    private static readonly Color CorOk = Color.FromArgb(30, 123, 52);
    private static readonly Color CorAtencao = Color.FromArgb(176, 112, 0);
    private static readonly Color CorErro = Color.FromArgb(180, 35, 24);
    private static readonly Color CorSecundaria = Color.FromArgb(90, 98, 110);
    private static readonly Color CorTexto = Color.FromArgb(30, 34, 40);
    private static readonly Color CorFundoSuave = Color.FromArgb(246, 248, 251);
    private const string Definida = "definida — deixe em branco para manter";
    private const string PortaPadrao = "1433";
    private const string SemAcesso = "Este usuário do Windows não tem acesso à configuração do integrador. Quem instalou abre direto; outro usuário: botão direito em \"Configurar Integrador IABS\" → Executar como administrador.";

    private readonly Caminhos _caminhos;
    private readonly ILogger _logger;
    private ConfiguracaoLocal? _atual;

    // conexão com a IABS
    private readonly TextBox _url = Campo();
    private readonly TextBox _chave = Campo(senha: true);
    private readonly Label _resultadoIabs = Resultado();
    private readonly Label _seloChave = Selo("✔ Chave definida");
    private readonly Label _seloSenha = Selo("✔ Senha definida");

    // banco do Senior
    private readonly TextBox _servidor = Campo();
    private readonly TextBox _porta = Campo();
    private readonly TextBox _nomeBanco = Campo();
    private readonly TextBox _schema = Campo();
    private readonly TextBox _instancia = Campo();
    private readonly TextBox _usuario = Campo();
    private readonly TextBox _senha = Campo(senha: true);
    private readonly CheckBox _gravacao = new()
    {
        Text = "Permitir que a IABS lance os atestados no Senior (tabela r038afa)", AutoSize = true, Margin = new Padding(3, 10, 3, 2),
    };
    private readonly ListView _tabelas = new()
    {
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Height = 210, Anchor = AnchorStyles.Left | AnchorStyles.Right, BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly Label _resultadoBanco = Resultado();

    // situação
    private readonly Label _servicoEstado = Valor();
    private readonly Label _canal = Valor();
    private readonly Label _conexaoIabs = Valor();
    private readonly Label _ultimoPedido = Valor();
    private readonly Label _pedidos = Valor();
    private readonly Label _lancamento = Valor();
    private readonly Label _pasta = Valor();
    private readonly Button _iniciarServico = Botao("Iniciar serviço");

    // rodapé
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CorSecundaria, AutoEllipsis = true };
    private readonly Button _salvar = Botao("Salvar", destaque: true);
    private readonly List<Button> _botoesAcao = [];

    public JanelaPrincipal(Caminhos caminhos)
    {
        _caminhos = caminhos;
        _logger = LoggerFactory.Create(b => b.AddProvider(new ArquivoLoggerProvider(caminhos.PastaLogs))).CreateLogger("Configurador");

        Text = "Configurar Integrador IABS";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(860, 700);
        MinimumSize = new Size(780, 620);
        BackColor = Color.White;

        // o controle Fill entra primeiro; topo e rodapé depois
        Controls.Add(MontarAbas());
        Controls.Add(MontarRodape());
        Controls.Add(MontarCabecalho());
        _botoesAcao.Add(_salvar);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        CarregarConfiguracao();
        AtualizarSituacao();
    }

    // ---- montagem da tela ----------------------------------------------------------
    private static TextBox Campo(bool senha = false) => new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right, UseSystemPasswordChar = senha, Margin = new Padding(3, 4, 3, 4),
    };

    private static Label Resultado() => new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 8, 3, 4) };

    private static Label Valor() => new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 7, 3, 5) };

    /// <summary>Selo verde ao lado de um segredo já gravado (chave ou senha nunca aparecem).</summary>
    private static Label Selo(string texto) => new()
    {
        Text = texto, AutoSize = true, Visible = false, ForeColor = Color.White, BackColor = CorOk,
        Font = new Font("Segoe UI Semibold", 8.5f), Padding = new Padding(8, 3, 8, 3), Margin = new Padding(8, 5, 3, 3),
        Anchor = AnchorStyles.Left,
    };

    /// <summary>Campo de texto com o selo à direita.</summary>
    private static TableLayoutPanel ComSelo(TextBox campo, Label selo)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.Controls.Add(campo, 0, 0);
        t.Controls.Add(selo, 1, 0);
        return t;
    }

    private static Button Botao(string texto, bool destaque = false)
    {
        var b = new Button
        {
            Text = texto, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat,
            Padding = new Padding(12, 5, 12, 5), Margin = new Padding(0, 3, 8, 3), Cursor = Cursors.Hand,
            BackColor = destaque ? CorDestaque : Color.FromArgb(240, 243, 247),
            ForeColor = destaque ? Color.White : CorTexto,
        };
        b.FlatAppearance.BorderColor = destaque ? CorDestaque : Color.FromArgb(200, 206, 214);
        return b;
    }

    private static FlowLayoutPanel Fluxo(params Control[] controles)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 6, 0, 2), Anchor = AnchorStyles.Left };
        f.Controls.AddRange(controles);
        return f;
    }

    private static TableLayoutPanel Grade()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(4, 4, 18, 4) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static void Linha(TableLayoutPanel t, string rotulo, Control campo, string? dica = null)
    {
        var l = new Label { Text = rotulo, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 4) };
        var r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(l, 0, r);
        t.Controls.Add(campo, 1, r);
        if (dica is null) return;
        var d = new Label { Text = dica, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = CorSecundaria, Margin = new Padding(3, 0, 3, 8), Font = new Font("Segoe UI", 8.5f) };
        var r2 = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(d, 1, r2);
    }

    private Control MontarCabecalho()
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = CorDestaque };
        p.Controls.Add(new Label
        {
            Text = "Integrador IABS", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15f),
            AutoSize = true, Location = new Point(22, 10),
        });
        p.Controls.Add(new Label
        {
            Text = $"Ponte entre a IABS e o banco do Senior  ·  versão {Constantes.Versao}  ·  só conexão de saída",
            ForeColor = Color.FromArgb(205, 218, 235), AutoSize = true, Location = new Point(24, 46),
        });
        return p;
    }

    private Control MontarRodape()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 60, ColumnCount = 3, Padding = new Padding(18, 12, 10, 10), BackColor = CorFundoSuave };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var fechar = Botao("Fechar");
        fechar.Click += (_, _) => Close();
        _salvar.Click += async (_, _) => await SalvarAsync();
        t.Controls.Add(_status, 0, 0);
        t.Controls.Add(_salvar, 1, 0);
        t.Controls.Add(fechar, 2, 0);
        return t;
    }

    private Control MontarAbas()
    {
        var abas = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 6) };
        abas.TabPages.Add(Aba("1. Conexão com a IABS", MontarConexao()));
        abas.TabPages.Add(Aba("2. Banco do Senior", MontarBanco()));
        var situacao = Aba("3. Situação", MontarSituacao());
        abas.TabPages.Add(situacao);
        abas.SelectedIndexChanged += (_, _) =>
        {
            if (abas.SelectedTab == situacao) AtualizarSituacao();
        };
        var moldura = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 8) };
        moldura.Controls.Add(abas);
        return moldura;
    }

    private static TabPage Aba(string titulo, Control conteudo)
    {
        var p = new TabPage(titulo) { Padding = new Padding(14), BackColor = Color.White, UseVisualStyleBackColor = false };
        p.Controls.Add(conteudo);
        return p;
    }

    private Control MontarConexao()
    {
        var t = Grade();
        _url.PlaceholderText = "https://<endereço da IABS>";
        Linha(t, "Endereço da IABS", _url, dica: "O mesmo endereço em que a IABS é usada no navegador. Aparece na tela da chave, na IABS.");
        Linha(t, "Chave do integrador", ComSelo(_chave, _seloChave),
            dica: "Na IABS: Governança → Configurações → Integrações → conexão \"Integrador IABS\" → Gerar chave. Aparece uma vez só.");
        var testar = Botao("Testar conexão");
        testar.Click += async (_, _) => await TestarIabsAsync();
        _botoesAcao.Add(testar);
        Linha(t, "", Fluxo(testar));
        Linha(t, "", _resultadoIabs);
        return t;
    }

    private Control MontarBanco()
    {
        var t = Grade();
        Linha(t, "Servidor", _servidor, dica: "Nome ou IP do SQL Server do Senior, na rede interna.");
        // porta padrão do SQL Server já preenchida (pedido de 26/11/2026); com instância nomeada, a TI apaga
        _porta.Text = PortaPadrao;
        _porta.PlaceholderText = "vazio = usar a instância";
        Linha(t, "Porta", _porta, dica: "Padrão do SQL Server: 1433. Com instância nomeada, apague a porta e informe a instância.");
        Linha(t, "Nome do banco", _nomeBanco);
        _schema.Text = "dbo";
        Linha(t, "Schema das tabelas", _schema);
        _instancia.PlaceholderText = "vazio = instância padrão (ignorada quando há porta)";
        Linha(t, "Instância", _instancia);
        Linha(t, "Usuário", _usuario);
        Linha(t, "Senha", ComSelo(_senha, _seloSenha), dica: "Fica só neste servidor, cifrada pelo Windows. A IABS nunca recebe a senha.");
        Linha(t, "", _gravacao,
            dica: "Desmarcado, a IABS só lê. Marcado, ela inclui, corrige e apaga só as linhas com a marca dela (\"IABS #…\" no campo de observação); " +
                  "os lançamentos do RH e do ponto nunca são alterados. O usuário do banco precisa poder incluir, alterar e excluir na r038afa.");
        var testar = Botao("Testar banco");
        testar.Click += async (_, _) => await TestarBancoAsync();
        _botoesAcao.Add(testar);
        Linha(t, "", Fluxo(testar));
        _tabelas.Columns.Add("Tabela", 170);
        _tabelas.Columns.Add("Situação", 330);
        Linha(t, "", _tabelas);
        Linha(t, "", _resultadoBanco);
        return t;
    }

    private Control MontarSituacao()
    {
        var t = Grade();
        _iniciarServico.Click += (_, _) => IniciarServico();
        Linha(t, "Serviço do Windows", Fluxo(_servicoEstado, _iniciarServico));
        Linha(t, "Conexão com a IABS", _canal);
        Linha(t, "Conexão na IABS", _conexaoIabs);
        Linha(t, "Último pedido", _ultimoPedido);
        Linha(t, "Pedidos atendidos", _pedidos, dica: "Desde que o serviço iniciou. O detalhe de cada pedido fica no log (sem dado de colaborador).");
        Linha(t, "Lançar atestados", _lancamento);
        Linha(t, "Pasta de dados", _pasta);
        var atualizar = Botao("Atualizar");
        atualizar.Click += (_, _) => AtualizarSituacao();
        var diagnostico = Botao("Gerar diagnóstico");
        diagnostico.Click += (_, _) => GerarDiagnostico();
        var logs = Botao("Abrir pasta de logs");
        logs.Click += (_, _) => AbrirPasta(_caminhos.PastaLogs);
        _botoesAcao.Add(diagnostico);
        Linha(t, "", Fluxo(atualizar, diagnostico, logs));
        return t;
    }

    // ---- configuração ----------------------------------------------------------------------
    private void CarregarConfiguracao()
    {
        try
        {
            _atual = ConfiguracaoLocal.Carregar(_caminhos);
        }
        catch (UnauthorizedAccessException)
        {
            Status(SemAcesso, erro: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Status($"Não consegui ler a configuração atual: {ex.Message}", erro: true);
            return;
        }
        if (_atual is null)
        {
            Status("Integrador ainda não configurado: preencha as abas 1 e 2 e clique em Salvar.");
            return;
        }

        _url.Text = _atual.ApiUrl;
        _chave.Text = "";
        _chave.PlaceholderText = string.IsNullOrEmpty(_atual.ChaveApiProtegida) ? "" : Definida;
        _seloChave.Visible = !string.IsNullOrEmpty(_atual.ChaveApiProtegida);

        var b = _atual.Banco;
        _servidor.Text = b.Servidor;
        _porta.Text = b.Porta?.ToString(CultureInfo.InvariantCulture) ?? "";
        _nomeBanco.Text = b.Banco ?? "";
        _schema.Text = b.Schema ?? "dbo";
        _instancia.Text = b.Instancia ?? "";
        _usuario.Text = b.Usuario ?? "";
        _usuario.Enabled = _senha.Enabled = !b.AutenticacaoWindows;
        _senha.Text = "";
        _senha.PlaceholderText = string.IsNullOrEmpty(b.SenhaProtegida) ? "" : Definida;
        _seloSenha.Visible = !string.IsNullOrEmpty(b.SenhaProtegida);
        _gravacao.Checked = _atual.GravacaoLiberada;
        Status($"Configuração carregada de {_caminhos.ArquivoConfig}.");
    }

    private static string? Nulo(string texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    /// <summary>
    /// O que está na tela. Autenticação do Windows, certificado e proxy não estão
    /// na tela (só pela linha de comando): o valor gravado é preservado.
    /// </summary>
    private EntradaConfiguracao LerCampos()
    {
        var windows = _atual?.Banco.AutenticacaoWindows ?? false;
        return new EntradaConfiguracao
        {
            ApiUrl = _url.Text.Trim(),
            ChaveApi = _chave.Text.Trim(),
            Banco = new ConfiguracaoBanco
            {
                Servidor = _servidor.Text.Trim(),
                Porta = int.TryParse(_porta.Text.Trim(), out var porta) ? porta : null,
                Banco = Nulo(_nomeBanco.Text),
                Schema = Nulo(_schema.Text) ?? "dbo",
                Instancia = Nulo(_instancia.Text),
                AutenticacaoWindows = windows,
                ConfiarCertificadoServidor = _atual?.Banco.ConfiarCertificadoServidor ?? false,
                Usuario = windows ? null : Nulo(_usuario.Text),
                TempoLimiteConsultaSegundos = _atual?.Banco.TempoLimiteConsultaSegundos ?? 120,
            },
            SenhaBanco = _senha.Text,
            Proxy = new ConfiguracaoProxy
            {
                Modo = _atual?.Proxy.Modo ?? "sistema",
                Endereco = _atual?.Proxy.Endereco,
                Usuario = _atual?.Proxy.Usuario,
            },
            GravacaoLiberada = _gravacao.Checked,
        };
    }

    /// <summary>Banco dos campos, com a senha digitada ou a já gravada (para testar antes de salvar).</summary>
    private ConfiguracaoBanco BancoParaTeste()
    {
        var e = LerCampos();
        e.Banco.SenhaProtegida = e.Banco.AutenticacaoWindows ? null
            : !string.IsNullOrEmpty(e.SenhaBanco) ? Protetor.Proteger(e.SenhaBanco)
            : _atual?.Banco.SenhaProtegida;
        return e.Banco;
    }

    private ConfiguracaoLocal IabsParaTeste()
    {
        var e = LerCampos();
        if (string.IsNullOrWhiteSpace(e.ApiUrl)) throw new InvalidOperationException("Informe o endereço da IABS.");
        var chave = !string.IsNullOrWhiteSpace(e.ChaveApi) ? Protetor.Proteger(e.ChaveApi) : _atual?.ChaveApiProtegida;
        if (string.IsNullOrEmpty(chave)) throw new InvalidOperationException("Informe a chave do integrador.");
        var proxy = e.Proxy!;
        proxy.SenhaProtegida = _atual?.Proxy.SenhaProtegida;
        return new ConfiguracaoLocal { ApiUrl = e.ApiUrl, ChaveApiProtegida = chave, Proxy = proxy, Banco = BancoParaTeste(), GravacaoLiberada = e.GravacaoLiberada };
    }

    private async Task<bool> TestarIabsAsync()
    {
        Ocupado(true, "Testando a conexão com a IABS...");
        try
        {
            var cfg = IabsParaTeste();
            var fim = await Task.Run(() => ServicoConfiguracao.TestarIabsAsync(cfg, _logger, CancellationToken.None));
            var ok = fim.Codigo == "OK";
            Mostrar(_resultadoIabs, ok, ok
                ? $"A IABS aceitou a chave. Conexão na IABS: \"{fim.ConexaoIabs}\"."
                : fim.Mensagem);
            return ok;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UriFormatException)
        {
            Mostrar(_resultadoIabs, false, ex.Message);
            return false;
        }
        finally
        {
            Ocupado(false);
        }
    }

    private async Task<bool> TestarBancoAsync()
    {
        Ocupado(true, "Testando o banco do Senior...");
        _tabelas.Items.Clear();
        try
        {
            var banco = BancoParaTeste();
            var r = await Task.Run(() => ServicoConfiguracao.TestarBancoAsync(banco, _logger, CancellationToken.None));
            foreach (var tab in r.Tabelas)
            {
                var item = new ListViewItem(tab.Tabela);
                item.SubItems.Add(tab.Ok ? "✔ lida" : $"✖ {Cli.TextoDoErro(tab.Codigo ?? "")}");
                item.ForeColor = tab.Ok ? CorOk : CorErro;
                _tabelas.Items.Add(item);
            }
            var permissao = new ListViewItem("r038afa (gravar)");
            permissao.SubItems.Add(r.GravacaoOk ? "✔ inclui, altera e exclui"
                : $"incluir {(r.Incluir ? "sim" : "não")}, alterar {(r.Alterar ? "sim" : "não")}, excluir {(r.Excluir ? "sim" : "não")}");
            permissao.ForeColor = r.GravacaoOk ? CorOk : _gravacao.Checked ? CorErro : CorSecundaria;
            _tabelas.Items.Add(permissao);

            var ok = r.LeituraOk && (!_gravacao.Checked || r.GravacaoOk);
            var texto = $"{r.Banco.Nome} — {r.Banco.Produto} {r.Banco.Versao}. "
                + (r.LeituraOk ? "Todas as tabelas são lidas. " : "Faltam permissões de leitura. ")
                + (_gravacao.Checked && !r.GravacaoOk ? "O lançamento está liberado, mas o usuário não pode incluir, alterar e excluir na r038afa. " : "")
                + (r.CertificadoNaoValidado ? "O certificado do SQL Server não é de autoridade reconhecida: a conexão é cifrada, sem validar o certificado." : "");
            Mostrar(_resultadoBanco, ok, texto);
            return ok;
        }
        catch (ErroOperacao ex)
        {
            Mostrar(_resultadoBanco, false, Cli.TextoDoErro(ex.Codigo));
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Mostrar(_resultadoBanco, false, ex.Message.Split('\n')[0]);
            return false;
        }
        finally
        {
            Ocupado(false);
        }
    }

    private async Task SalvarAsync()
    {
        ConfiguracaoLocal cfg;
        try
        {
            cfg = ServicoConfiguracao.Montar(_atual, LerCampos());
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Configuração incompleta", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var bancoOk = await TestarBancoAsync();
        var iabsOk = await TestarIabsAsync();
        if (!bancoOk || !iabsOk)
        {
            var falhou = !bancoOk && !iabsOk ? "o banco do Senior e a conexão com a IABS" : !bancoOk ? "o banco do Senior" : "a conexão com a IABS";
            var resposta = MessageBox.Show(this,
                $"O teste de {falhou} falhou. O resultado está nas abas 1 e 2.\n\nGravar a configuração mesmo assim?",
                "Teste com falha", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (resposta != DialogResult.Yes)
            {
                Status("Configuração não gravada.", erro: true);
                return;
            }
        }

        try
        {
            var aviso = ServicoConfiguracao.Gravar(cfg, _caminhos);
            CarregarConfiguracao();
            AtualizarSituacao();
            Status("Configuração gravada.");
            MessageBox.Show(this,
                "Configuração gravada.\n\nO serviço se conecta à IABS em até 15 segundos. Acompanhe na aba Situação."
                + (aviso is null ? "" : $"\n\n{aviso}"),
                "Integrador IABS", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, SemAcesso, "Sem acesso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Não consegui gravar: {ex.Message}", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- situação ------------------------------------------------------------------------------
    private void AtualizarSituacao()
    {
        try
        {
            using var sc = new ServiceController(Constantes.NomeServico);
            var s = sc.Status;
            _servicoEstado.Text = s switch
            {
                ServiceControllerStatus.Running => "● Em execução",
                ServiceControllerStatus.Stopped => "● Parado",
                _ => $"● {s}",
            };
            _servicoEstado.ForeColor = s == ServiceControllerStatus.Running ? CorOk : CorErro;
            _iniciarServico.Visible = s == ServiceControllerStatus.Stopped;
        }
        catch (InvalidOperationException)
        {
            _servicoEstado.Text = "● Não instalado (rode o instalador)";
            _servicoEstado.ForeColor = CorErro;
            _iniciarServico.Visible = false;
        }

        _pasta.Text = _caminhos.PastaDados;
        var st = StatusPainel.Ler(_caminhos);
        if (st is null || !st.Configurado)
        {
            _canal.Text = _atual is null ? "depois de salvar a configuração" : "aguardando o serviço";
            _canal.ForeColor = CorSecundaria;
            _conexaoIabs.Text = _ultimoPedido.Text = _pedidos.Text = "—";
            _lancamento.Text = _atual?.GravacaoLiberada == true ? "liberado" : "desligado (a IABS só lê)";
            return;
        }
        if (st.Conectado)
        {
            _canal.Text = $"● Conectado desde {st.ConectadoDesde?.LocalDateTime:dd/MM/yyyy HH:mm}";
            _canal.ForeColor = CorOk;
        }
        else
        {
            var desde = st.DesconectadoDesde is { } d ? $" desde {d.LocalDateTime:dd/MM/yyyy HH:mm}" : "";
            _canal.Text = $"● Desconectado{desde}. {st.Mensagem}";
            _canal.ForeColor = st.DesconectadoDesde is { } d2 && DateTimeOffset.Now - d2 > TimeSpan.FromMinutes(2) ? CorErro : CorAtencao;
        }
        _conexaoIabs.Text = st.ConexaoIabs ?? "—";
        _ultimoPedido.Text = st.UltimoPedidoEm is { } p ? p.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture) : "nenhum ainda";
        _pedidos.Text = st.Pedidos.ToString("N0", CultureInfo.CurrentCulture);
        _lancamento.Text = st.GravacaoLiberada ? "liberado" : "desligado (a IABS só lê)";
    }

    private void IniciarServico()
    {
        try
        {
            using var sc = new ServiceController(Constantes.NomeServico);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Status("Serviço iniciado.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            Status($"Não consegui iniciar o serviço: {ex.Message}", erro: true);
        }
        AtualizarSituacao();
    }

    private void GerarDiagnostico()
    {
        try
        {
            var zip = PacoteDiagnostico.Gerar(_caminhos);
            Status($"Diagnóstico gerado: {zip}");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status($"Não consegui gerar o diagnóstico: {ex.Message}", erro: true);
        }
    }

    private void AbrirPasta(string pasta)
    {
        try
        {
            Directory.CreateDirectory(pasta);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pasta}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status($"Não consegui abrir a pasta: {ex.Message}", erro: true);
        }
    }

    // ---- utilidades ----------------------------------------------------------------------------
    private void Ocupado(bool ocupado, string? mensagem = null)
    {
        UseWaitCursor = ocupado;
        foreach (var b in _botoesAcao) b.Enabled = !ocupado;
        if (mensagem is not null) Status(mensagem);
    }

    private static void Mostrar(Label alvo, bool ok, string texto)
    {
        alvo.Text = (ok ? "✔  " : "✖  ") + texto;
        alvo.ForeColor = ok ? CorOk : CorErro;
    }

    private void Status(string texto, bool erro = false)
    {
        _status.Text = texto;
        _status.ForeColor = erro ? CorErro : CorSecundaria;
    }
}
