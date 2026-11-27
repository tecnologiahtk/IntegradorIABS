using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using System.Text.Json;
using IabsAgenteSenior.Atualizacao;
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
/// Várias conexões (2.2.0): o seletor no topo escolhe a conexão das abas 1 e 2
/// (ex.: Produção e Homologação, cada uma com endereço, chave e banco
/// próprios); a aba 3 mostra todas. Salvar grava só a conexão escolhida.
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
    private ConfiguracaoIntegrador _arquivo = new();

    /// <summary>A conexão escolhida como está gravada (nula: conexão nova, ainda não salva).</summary>
    private ConfiguracaoLocal? _atual;

    /// <summary>Nome da conexão nova, ainda não salva (uma por vez).</summary>
    private string? _nova;

    /// <summary>A conexão mostrada nas abas 1 e 2.</summary>
    private string? _selecionada;

    /// <summary>Os campos como foram carregados: diferente = alteração não salva.</summary>
    private string _carregado = "";
    private bool _preenchendo;
    private bool _ocupado;

    // conexões (2.2.0)
    private readonly ComboBox _conexao = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Margin = new Padding(3, 4, 10, 3) };
    private readonly Button _novaConexao = Botao("+ Nova conexão");
    private readonly Button _removerConexao = Botao("Remover");
    private readonly TextBox _nome = Campo();

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
    // 2.1.4: o integrador não confia mais sozinho num certificado que não é de autoridade reconhecida
    private readonly CheckBox _confiarCertificado = new()
    {
        Text = "Confiar no certificado do SQL Server (só se o teste pedir)", AutoSize = true, Margin = new Padding(3, 6, 3, 2),
    };
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
    private readonly ListView _situacaoConexoes = new()
    {
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowItemToolTips = true,
        Height = 118, Anchor = AnchorStyles.Left | AnchorStyles.Right, BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly Label _pasta = Valor();
    private readonly Button _iniciarServico = Botao("Iniciar serviço");

    // versão e atualização (2.1.0)
    private readonly Label _versaoInstalada = Valor();
    private readonly Button _verificar = Botao("Verificar atualização");
    private readonly Button _atualizarAgora = Botao("Atualizar agora", destaque: true);
    private readonly Label _situacaoAtualizacao = Resultado();
    private readonly System.Windows.Forms.Timer _acompanharAtualizacao = new() { Interval = 2_000 };
    private VersaoPublicada? _disponivel;
    private DateTimeOffset _atualizadorLigadoEm;
    private int _esperasDaAtualizacao;

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
        ClientSize = new Size(860, 740);
        MinimumSize = new Size(780, 660);
        BackColor = Color.White;

        // o controle Fill entra primeiro; topo e rodapé depois (o último do topo fica em cima)
        Controls.Add(MontarAbas());
        Controls.Add(MontarRodape());
        Controls.Add(MontarSeletor());
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

    /// <summary>Seletor de conexão (2.2.0), entre o cabeçalho e as abas.</summary>
    private Control MontarSeletor()
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(26, 10, 16, 4), WrapContents = false, BackColor = CorFundoSuave };
        var rotulo = new Label { Text = "Conexão", AutoSize = true, Margin = new Padding(0, 8, 6, 3), Font = new Font("Segoe UI Semibold", 9.5f) };
        _conexao.SelectedIndexChanged += (_, _) =>
        {
            if (!_preenchendo) TrocarConexao();
        };
        _novaConexao.Click += (_, _) => NovaConexao();
        _removerConexao.Click += (_, _) => RemoverConexao();
        var dica = new Label
        {
            Text = "As abas 1 e 2 são da conexão escolhida; a 3 mostra todas.", AutoSize = true, ForeColor = CorSecundaria,
            Margin = new Padding(6, 9, 3, 3), Font = new Font("Segoe UI", 8.5f),
        };
        p.Controls.AddRange([rotulo, _conexao, _novaConexao, _removerConexao, dica]);
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
        Linha(t, "Nome da conexão", _nome, dica: "Como a conexão aparece aqui, no ícone perto do relógio e no log. Ex.: Produção, Homologação.");
        _url.PlaceholderText = "https://<endereço da IABS>";
        Linha(t, "Endereço da IABS", _url,
            dica: "O mesmo endereço em que a IABS desta conexão é usada no navegador (o da produção ou o da homologação). Aparece na tela da chave, na IABS.");
        Linha(t, "Chave do integrador", ComSelo(_chave, _seloChave),
            dica: "Na IABS: Governança → Configurações → Integrações → conexão \"Integrador IABS\" → Gerar chave. Aparece uma vez só. " +
                  "Cada conexão usa a chave gerada no seu próprio ambiente da IABS.");
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
        Linha(t, "", _confiarCertificado,
            dica: "Desmarcado, o certificado do SQL Server é validado. Marque só se o teste disser que o certificado não é de uma autoridade " +
                  "reconhecida e o servidor for mesmo o do Senior de vocês: a conexão continua cifrada, sem validar o certificado.");
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
        _situacaoConexoes.Columns.Add("Conexão", 100);
        _situacaoConexoes.Columns.Add("Situação", 185);
        _situacaoConexoes.Columns.Add("Na IABS", 100);
        _situacaoConexoes.Columns.Add("Último pedido", 90);
        _situacaoConexoes.Columns.Add("Pedidos", 50);
        _situacaoConexoes.Columns.Add("Lançar", 55);
        Linha(t, "Conexões com a IABS", _situacaoConexoes,
            dica: "Pedidos atendidos desde que o serviço iniciou. O detalhe de cada pedido fica no log (sem dado de colaborador).");
        Linha(t, "Pasta de dados", _pasta);
        // versão e atualização (2.1.0)
        _versaoInstalada.Text = Constantes.Versao;
        _verificar.Click += async (_, _) => await VerificarAtualizacaoAsync();
        _atualizarAgora.Click += (_, _) => AtualizarAgora();
        _atualizarAgora.Visible = false;
        _botoesAcao.AddRange([_verificar, _atualizarAgora]);
        Linha(t, "Versão instalada", Fluxo(_versaoInstalada, _verificar, _atualizarAgora),
            dica: "A versão nova vem só do repositório oficial (github.com/" + Constantes.RepositorioOficial + ") e só é instalada se o SHA-256 conferir.");
        Linha(t, "", _situacaoAtualizacao);
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
    /// <summary>Lê o arquivo e mostra a conexão <paramref name="escolher"/> (ou a primeira).</summary>
    private void CarregarConfiguracao(string? escolher = null)
    {
        try
        {
            _arquivo = ConfiguracaoIntegrador.Carregar(_caminhos) ?? new ConfiguracaoIntegrador();
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
        // sem nenhuma conexão gravada, a primeira nasce como "Produção"
        _nova = _arquivo.Conexoes.Count == 0 ? ConfiguracaoLocal.NomePadrao : null;
        var nome = _arquivo.Achar(escolher)?.Nome ?? _arquivo.Conexoes.FirstOrDefault()?.Nome ?? _nova!;
        PreencherSeletor(nome);
        MostrarConexao(nome);
        Status(_arquivo.Conexoes.Count == 0
            ? "Integrador ainda não configurado: preencha as abas 1 e 2 e clique em Salvar."
            : $"Configuração carregada de {_caminhos.ArquivoConfig} ({(_arquivo.Conexoes.Count == 1 ? "1 conexão" : $"{_arquivo.Conexoes.Count} conexões")}).");
    }

    private void PreencherSeletor(string selecionar)
    {
        _preenchendo = true;
        try
        {
            _conexao.Items.Clear();
            foreach (var c in _arquivo.Conexoes) _conexao.Items.Add(c.Nome);
            if (_nova is not null) _conexao.Items.Add(_nova);
            _conexao.SelectedItem = selecionar;
        }
        finally
        {
            _preenchendo = false;
        }
    }

    /// <summary>Mostra a conexão nas abas 1 e 2 (conexão nova: campos vazios, com a porta e o schema padrão).</summary>
    private void MostrarConexao(string nome)
    {
        _selecionada = nome;
        _atual = _arquivo.Achar(nome);
        var c = _atual;
        _nome.Text = nome;
        _url.Text = c?.ApiUrl ?? "";
        _chave.Text = "";
        _chave.PlaceholderText = string.IsNullOrEmpty(c?.ChaveApiProtegida) ? "" : Definida;
        _seloChave.Visible = !string.IsNullOrEmpty(c?.ChaveApiProtegida);

        var b = c?.Banco;
        _servidor.Text = b?.Servidor ?? "";
        _porta.Text = b is null ? PortaPadrao : b.Porta?.ToString(CultureInfo.InvariantCulture) ?? "";
        _nomeBanco.Text = b?.Banco ?? "";
        _schema.Text = b?.Schema ?? "dbo";
        _instancia.Text = b?.Instancia ?? "";
        _usuario.Text = b?.Usuario ?? "";
        _usuario.Enabled = _senha.Enabled = b?.AutenticacaoWindows != true;
        _senha.Text = "";
        _senha.PlaceholderText = string.IsNullOrEmpty(b?.SenhaProtegida) ? "" : Definida;
        _seloSenha.Visible = !string.IsNullOrEmpty(b?.SenhaProtegida);
        _gravacao.Checked = c?.GravacaoLiberada ?? false;
        _confiarCertificado.Checked = b?.ConfiarCertificadoServidor ?? false;

        // os resultados dos testes eram da conexão anterior
        _resultadoIabs.Text = _resultadoBanco.Text = "";
        _tabelas.Items.Clear();
        _carregado = Assinatura();
        AjustarBotoesDeConexao();
    }

    /// <summary>Os campos da tela, para saber se há alteração não salva (só em memória).</summary>
    private string Assinatura() => JsonSerializer.Serialize(LerCampos());

    private bool TemAlteracao() => _selecionada is not null && Assinatura() != _carregado;

    /// <summary>Sem alteração, ou a pessoa aceitou descartar.</summary>
    private bool PodeSairDaConexao() =>
        !TemAlteracao()
        || MessageBox.Show(this, $"A conexão \"{_selecionada}\" tem alterações que não foram salvas.\n\nDescartar as alterações?",
            "Alterações não salvas", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void TrocarConexao()
    {
        if (_conexao.SelectedItem is not string destino || destino == _selecionada) return;
        if (!PodeSairDaConexao())
        {
            PreencherSeletor(_selecionada!);
            return;
        }
        if (_nova is not null && _selecionada == _nova && _arquivo.Conexoes.Count > 0)
        {
            // a conexão nova que não foi salva sai da lista
            _nova = null;
            PreencherSeletor(destino);
        }
        MostrarConexao(destino);
        Status($"Conexão \"{destino}\".");
    }

    private void NovaConexao()
    {
        if (!PodeSairDaConexao()) return;
        var nome = PerguntarNome(_arquivo.Achar("Homologação") is null ? "Homologação" : "");
        if (nome is null) return;
        try
        {
            nome = ServicoConfiguracao.NomeValido(nome);
            if (_arquivo.Achar(nome) is not null) throw new InvalidOperationException($"Já existe uma conexão \"{nome}\".");
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Nova conexão", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _nova = nome;
        PreencherSeletor(nome);
        MostrarConexao(nome);
        Status($"Conexão nova \"{nome}\": preencha as abas 1 e 2 com o endereço e a chave do ambiente da IABS e o banco do Senior dela, e clique em Salvar.");
    }

    private string? PerguntarNome(string sugestao)
    {
        using var f = new Form
        {
            Text = "Nova conexão", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(440, 160), BackColor = Color.White,
        };
        var corpo = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 14, 16, 4) };
        corpo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        corpo.Controls.Add(new Label
        {
            Text = "Nome da conexão (ex.: Homologação). Ela terá endereço da IABS, chave e banco do Senior próprios.",
            AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 0, 0, 8),
        });
        var campo = new TextBox { Text = sugestao, Anchor = AnchorStyles.Left | AnchorStyles.Right, MaxLength = 40 };
        corpo.Controls.Add(campo);
        var ok = Botao("Criar", destaque: true);
        ok.DialogResult = DialogResult.OK;
        var cancelar = Botao("Cancelar");
        cancelar.DialogResult = DialogResult.Cancel;
        var botoes = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(12, 8, 6, 8), BackColor = CorFundoSuave };
        botoes.Controls.AddRange([cancelar, ok]);
        f.Controls.Add(corpo);
        f.Controls.Add(botoes);
        f.AcceptButton = ok;
        f.CancelButton = cancelar;
        return f.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(campo.Text) ? campo.Text : null;
    }

    private void RemoverConexao()
    {
        var nome = _selecionada;
        if (nome is null) return;
        if (nome == _nova)
        {
            // ainda não salva: só sai da lista
            CarregarConfiguracao();
            return;
        }
        if (_arquivo.Conexoes.Count <= 1) return;
        var resposta = MessageBox.Show(this,
            $"Remover a conexão \"{nome}\"?\n\nO serviço fecha o canal dela com a IABS em até 15 segundos, e o endereço, a chave e a senha do banco " +
            "dela saem deste servidor. As outras conexões continuam.",
            "Remover conexão", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (resposta != DialogResult.Yes) return;
        try
        {
            var aviso = ServicoConfiguracao.Remover(nome, _caminhos);
            CarregarConfiguracao();
            AtualizarSituacao();
            Status($"Conexão \"{nome}\" removida." + (aviso is null ? "" : $" {aviso}"));
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, SemAcesso, "Sem acesso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Não consegui remover: {ex.Message}", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>A única conexão gravada não sai; a nova, ainda não salva, sai quando há outra.</summary>
    private void AjustarBotoesDeConexao()
    {
        _conexao.Enabled = _novaConexao.Enabled = !_ocupado;
        _removerConexao.Enabled = !_ocupado && (_selecionada is not null && _selecionada == _nova
            ? _arquivo.Conexoes.Count > 0
            : _arquivo.Conexoes.Count > 1);
    }

    private static string? Nulo(string texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    /// <summary>
    /// O que está na tela. Autenticação do Windows e proxy não estão na tela (só
    /// pela linha de comando): o valor gravado é preservado.
    /// </summary>
    private EntradaConfiguracao LerCampos()
    {
        var windows = _atual?.Banco.AutenticacaoWindows ?? false;
        // conexão nova: o proxy da máquina (o da primeira conexão)
        var proxy = _atual?.Proxy ?? _arquivo.ProxyPrincipal;
        return new EntradaConfiguracao
        {
            Nome = _nome.Text.Trim(),
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
                ConfiarCertificadoServidor = _confiarCertificado.Checked,
                Usuario = windows ? null : Nulo(_usuario.Text),
                TempoLimiteConsultaSegundos = _atual?.Banco.TempoLimiteConsultaSegundos ?? 120,
            },
            SenhaBanco = _senha.Text,
            Proxy = new ConfiguracaoProxy
            {
                Modo = proxy.Modo,
                Endereco = proxy.Endereco,
                Usuario = proxy.Usuario,
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
        proxy.SenhaProtegida = (_atual?.Proxy ?? _arquivo.ProxyPrincipal).SenhaProtegida;
        return new ConfiguracaoLocal
        {
            Nome = e.Nome ?? "", ApiUrl = e.ApiUrl, ChaveApiProtegida = chave, Proxy = proxy, Banco = BancoParaTeste(), GravacaoLiberada = e.GravacaoLiberada,
        };
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
                + (r.CertificadoNaoValidado ? "Confiar no certificado está marcado: a conexão é cifrada, sem validar o certificado." : "");
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
        var nomeAntes = _atual?.Nome; // nulo: conexão nova
        ConfiguracaoLocal cfg;
        try
        {
            // conexão nova: parte do proxy da máquina (o da primeira conexão), com a senha cifrada
            cfg = ServicoConfiguracao.Montar(_atual ?? ServicoConfiguracao.BaseDaNova(_arquivo), LerCampos());
            ServicoConfiguracao.ConferirConflitos(_arquivo, cfg, nomeAntes);
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
            var aviso = ServicoConfiguracao.Gravar(cfg, _caminhos, substitui: nomeAntes);
            CarregarConfiguracao(cfg.Nome);
            AtualizarSituacao();
            Status($"Conexão \"{cfg.Nome}\" gravada.");
            MessageBox.Show(this,
                $"Conexão \"{cfg.Nome}\" gravada.\n\nO serviço se conecta à IABS em até 15 segundos. Acompanhe na aba Situação."
                + (_arquivo.Conexoes.Count > 1 ? "\n\nAs outras conexões continuam conectadas." : "")
                + (aviso is null ? "" : $"\n\n{aviso}"),
                "Integrador IABS", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (InvalidOperationException ex)
        {
            // outra janela gravou uma conexão com o mesmo nome ou a mesma chave no meio
            MessageBox.Show(this, ex.Message, "Configuração não gravada", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, SemAcesso, "Sem acesso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
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
        // a última atualização (2.1.0), quando não há uma em andamento nesta janela
        if (!_acompanharAtualizacao.Enabled && ArquivosAtualizacao.LerSituacao(_caminhos) is { } a)
        {
            if (a.Situacao == "INSTALANDO" && a.Versao == Constantes.Versao)
            {
                Mostrar(_situacaoAtualizacao, true, $"Atualizada para a versão {a.Versao} em {a.Em.LocalDateTime:dd/MM/yyyy HH:mm}.");
            }
            else if (a.Situacao == "INSTALANDO")
            {
                Mostrar(_situacaoAtualizacao, false, $"A atualização para {a.Versao} não terminou: veja o log do instalador em {Path.Combine(AreaDoSistema.PastaDoSystem, "atualizacao")} (só administradores abrem).");
            }
            else if (a.Situacao == "ERRO")
            {
                Mostrar(_situacaoAtualizacao, false, "Última tentativa: " + TextoDaAtualizacao(a));
            }
        }
        // uma linha por conexão gravada (2.2.0), com o que o serviço gravou no status.json
        var st = StatusPainel.Ler(_caminhos);
        _situacaoConexoes.BeginUpdate();
        try
        {
            _situacaoConexoes.Items.Clear();
            if (_arquivo.Conexoes.Count == 0)
            {
                var vazio = new ListViewItem("—") { ForeColor = CorSecundaria };
                vazio.SubItems.Add("depois de salvar a configuração");
                _situacaoConexoes.Items.Add(vazio);
            }
            foreach (var c in _arquivo.Conexoes)
            {
                var s = st?.Lista.FirstOrDefault(x => ConfiguracaoIntegrador.MesmoNome(x.Nome, c.Nome));
                var (texto, cor) = SituacaoDaConexao(s);
                var item = new ListViewItem(c.Nome) { ForeColor = cor, ToolTipText = texto };
                item.SubItems.Add(texto);
                item.SubItems.Add(s?.ConexaoIabs ?? "—");
                item.SubItems.Add(s is null ? "—" : s.UltimoPedidoEm is { } p ? p.LocalDateTime.ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture) : "nenhum ainda");
                item.SubItems.Add(s is null ? "—" : s.Pedidos.ToString("N0", CultureInfo.CurrentCulture));
                item.SubItems.Add(c.GravacaoLiberada ? "liberado" : "só lê");
                _situacaoConexoes.Items.Add(item);
            }
        }
        finally
        {
            _situacaoConexoes.EndUpdate();
        }
    }

    private static (string Texto, Color Cor) SituacaoDaConexao(StatusConexao? s)
    {
        if (s is null) return ("aguardando o serviço", CorSecundaria);
        if (s.Conectado) return ($"● Conectado desde {s.ConectadoDesde?.LocalDateTime:dd/MM/yyyy HH:mm}", CorOk);
        var desde = s.DesconectadoDesde is { } d ? $" desde {d.LocalDateTime:dd/MM/yyyy HH:mm}" : "";
        var longa = s.DesconectadoDesde is { } d2 && DateTimeOffset.Now - d2 > TimeSpan.FromMinutes(2);
        return ($"● Desconectado{desde}. {s.Mensagem}", longa ? CorErro : CorAtencao);
    }

    // ---- atualização (2.1.0) ------------------------------------------------------------------
    private async Task VerificarAtualizacaoAsync()
    {
        Ocupado(true, "Consultando a última versão publicada...");
        _atualizarAgora.Visible = false;
        _disponivel = null;
        try
        {
            var proxy = _arquivo.ProxyPrincipal;
            var v = await Task.Run(() => VerificadorAtualizacao.UltimaVersaoAsync(proxy, CancellationToken.None));
            if (v is null)
            {
                Mostrar(_situacaoAtualizacao, false, "Não achei versão publicada com instalador no repositório oficial.");
            }
            else if (RegrasAtualizacao.MaisNova(v.Versao, Constantes.Versao))
            {
                _disponivel = v;
                _atualizarAgora.Text = $"Atualizar para {v.Versao}";
                _atualizarAgora.Visible = true;
                Mostrar(_situacaoAtualizacao, true, $"Versão {v.Versao} disponível (a instalada é a {Constantes.Versao}).");
            }
            else
            {
                Mostrar(_situacaoAtualizacao, true, $"Esta é a versão mais nova ({Constantes.Versao}).");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Mostrar(_situacaoAtualizacao, false, "Não consegui consultar o GitHub. Confira a rede, o proxy e o firewall (libere github.com, api.github.com e *.githubusercontent.com).");
        }
        finally
        {
            Ocupado(false);
        }
    }

    private void AtualizarAgora()
    {
        if (_disponivel is null) return;
        var resposta = MessageBox.Show(this,
            $"Instalar a versão {_disponivel.Versao} do Integrador IABS?\n\n" +
            "O instalador é baixado do repositório oficial e conferido pelo SHA-256. Durante a instalação o integrador fica " +
            "desconectado por alguns instantes e esta janela fecha; depois, abra de novo pelo atalho. A configuração fica.",
            "Atualizar o Integrador IABS", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (resposta != DialogResult.Yes) return;
        try
        {
            // 2.2.1: a janela só liga o atualizador (qualquer usuário logado pode); ele confere tudo sozinho
            _atualizadorLigadoEm = DateTimeOffset.Now.AddSeconds(-2);
            VerificadorAtualizacao.LigarAtualizador(_disponivel);
            _atualizarAgora.Visible = false;
            _esperasDaAtualizacao = 0;
            Mostrar(_situacaoAtualizacao, true, $"Atualizador ligado: conferindo a versão {_disponivel.Versao} no GitHub...");
            _acompanharAtualizacao.Tick -= AcompanharAtualizacao;
            _acompanharAtualizacao.Tick += AcompanharAtualizacao;
            _acompanharAtualizacao.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Mostrar(_situacaoAtualizacao, false, ex is System.ComponentModel.Win32Exception or InvalidOperationException { InnerException: System.ComponentModel.Win32Exception }
                ? "Não consegui ligar o atualizador. Rode o instalador novo uma vez como administrador: ele libera o botão para todos os usuários."
                : ex.Message);
        }
    }

    private void AcompanharAtualizacao(object? sender, EventArgs e)
    {
        var s = ArquivosAtualizacao.LerSituacao(_caminhos);
        // só conta o que o atualizador gravou depois de ligado
        if (s is null || s.Em < _atualizadorLigadoEm)
        {
            if (++_esperasDaAtualizacao < 45) return;
            _acompanharAtualizacao.Stop();
            Mostrar(_situacaoAtualizacao, false,
                $"O atualizador não deu notícia em 90 segundos: nada foi instalado. Veja o log dele em {Path.Combine(AreaDoSistema.PastaDoSystem, "logs")} (só administradores abrem).");
            return;
        }
        var texto = TextoDaAtualizacao(s);
        Mostrar(_situacaoAtualizacao, s.Situacao != "ERRO", texto);
        if (s.Situacao == "ERRO") _acompanharAtualizacao.Stop();
    }

    private static string TextoDaAtualizacao(SituacaoAtualizacao s) => s.Situacao switch
    {
        "PEDIDA" => $"Atualização para {s.Versao} pedida: aguardando o atualizador...",
        "BAIXANDO" => $"Baixando e conferindo a versão {s.Versao}...",
        "INSTALANDO" => $"Instalando a versão {s.Versao}: o serviço reinicia e esta janela fecha em instantes.",
        "ERRO" => $"A atualização para {s.Versao} não foi feita ({s.Codigo}). Nada foi instalado.",
        _ => $"Atualização para {s.Versao}: {s.Situacao}.",
    };

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
        _ocupado = ocupado;
        foreach (var b in _botoesAcao) b.Enabled = !ocupado;
        AjustarBotoesDeConexao();
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
