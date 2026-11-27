# Integrador IABS

Programa da IABS instalado num servidor Windows do cliente. É a ponte entre a IABS e o banco do Senior (SQL Server) na rede interna do cliente, sem VPN e sem abrir porta de entrada.

**[Baixar o instalador (última versão)](https://github.com/tecnologiahtk/IntegradorIABS/releases/latest/download/IntegradorIABS.msi)** · [todas as versões](https://github.com/tecnologiahtk/IntegradorIABS/releases)

O código está aberto para leitura: a TI do cliente pode conferir aqui tudo o que o programa faz (ver [LICENCA.md](LICENCA.md)).

## O que ele faz

- Abre **uma conexão de saída** (WebSocket sobre HTTPS) com a IABS e a mantém aberta. Nenhuma porta de entrada é aberta no servidor.
- Atende **mais de um ambiente** na mesma instalação (desde a 2.2.0): ex.: a produção da IABS com o Senior de produção e a homologação da IABS com o Senior de homologação, cada conexão com endereço, chave e banco próprios.
- Atende só **operações fixas**, com o SQL dentro do programa: ler os colaboradores e conferir, lançar, corrigir e apagar afastamentos na tabela `r038afa`. A IABS manda o nome da operação e os parâmetros, **nunca SQL**. A lista completa está em [`docs/protocolo-canal.md`](docs/protocolo-canal.md) e o SQL em [`OperacoesSenior.cs`](agente/src/IabsAgenteSenior/Banco/OperacoesSenior.cs).
- Guarda a **senha do banco só no servidor do cliente**, cifrada pelo Windows. A IABS só conhece a chave do integrador, que ela mesma gera.
- **Atualiza-se quando a IABS pede** (desde a 2.1.0): um serviço separado, o atualizador, baixa a versão nova **só deste repositório** e só instala se o SHA-256 do instalador for o mesmo em três lugares: o que a IABS mandou, o publicado aqui no GitHub e o do arquivo baixado. Só versão mais nova. O código está em [`agente/src/IabsAgenteSenior/Atualizacao`](agente/src/IabsAgenteSenior/Atualizacao).

## O que ele nunca faz

- Rodar SQL vindo de fora.
- Gravar em outra tabela que não a `r038afa`.
- Alterar ou apagar linha sem a marca da IABS ("IABS #…" no campo de observação). Os lançamentos do RH e do ponto nunca são tocados.
- Gravar qualquer coisa sem a caixa **"Permitir que a IABS lance os atestados"** marcada pela TI.
- Mandar à IABS o texto da observação do RH ou a mensagem de erro do banco.
- Abrir porta de entrada, ou registrar CPF, nome, CID ou e-mail em log.
- Baixar e executar código, a não ser o instalador oficial deste repositório, conferido como acima. O único programa que ele chama é o `msiexec` do Windows, para instalar esse arquivo.

## Instalar

1. Rode o `IntegradorIABS.msi` como administrador (Windows Server 2019 ou mais novo, 64 bits) e escolha a pasta de instalação. O controle de conta de usuário aparece só aqui.
2. No fim abre a janela **Configurar Integrador IABS** (depois, pelo atalho **Integrador IABS** na Área de Trabalho):
   - **aba 1:** o endereço e a chave que a IABS entregou;
   - **aba 2:** o SQL Server do Senior (porta padrão 1433) e, se a empresa quiser que a IABS lance os atestados, a caixa de liberação.
3. Testar e Salvar. Em até 15 segundos o integrador se conecta à IABS.
4. Outro ambiente (ex.: homologação): **+ Nova conexão** no topo da janela, as abas 1 e 2 com o endereço, a chave e o banco dele, e Salvar.

O que pedir à TI e os detalhes de instalação estão em [`agente/LEIAME.md`](agente/LEIAME.md).

## Conferir um instalador

Cada versão traz nas notas o SHA-256 do `.msi`. Para conferir o código, compile a mesma versão (marcada no repositório) com o .NET SDK 10, sem modificar nada, seguindo [`agente/LEIAME.md`](agente/LEIAME.md).

## Licença

Código visível, direitos reservados: pode ler e conferir; não pode modificar nem reaproveitar. Ver [LICENCA.md](LICENCA.md).
