# Port / Install

Instalador de arquivos compartilhado pelos ports de jogos para PC.

Uma janela local para escolher a ISO ou pasta completa, acompanhar extração/cópia, verificação SHA-256, progresso, tempo decorrido e término estimado. Tema editorial em verde escuro e papel, sem navegador ou serviço remoto. Python 3.11+ / Tk, apenas biblioteca padrão em execução.

## Usar em um jogo

Abra `Instalar-Arquivos.cmd` na raiz do port. Ele usa a configuração `installer.json` e o submódulo fixado em `tools/port-installer`. Para um checkout novo:

```sh
git submodule update --init tools/port-installer
python tools/port-installer/installer.py --config installer.json
```

O instalador coloca todos os arquivos em `game_files/`. A ISO não é copiada para a instalação. Só depois de verificar tudo ele pergunta se você quer apagar permanentemente a ISO original (padrão: **Não**). Pastas de origem sempre são preservadas. Arquivos existentes com conteúdo diferente não são sobrescritos; os já idênticos são comparados e reutilizados.

ISO9660 de setores de 2048 bytes (PS2) e XDVDFS (Xbox/Xbox 360; XISO, XGD1/2/3) são suportados. CHD, BIN/CUE e imagens criptografadas precisam ser convertidos por ferramentas próprias antes. Cada jogo valida seus executáveis e sua revisão por SHA-256 antes de copiar dados. A configuração deve conter `required_files`; isso identifica a revisão, mas não prova que uma pasta fornecida contém todos os recursos: `required_paths` cobre somente os diretórios explicitados.

Cancelamento remove o arquivo temporário corrente. Arquivos já verificados permanecem para retomar com a mesma origem. O relatório `game-install-report.json` registra hashes, conclusão e a decisão de apagar a ISO. Um bloqueio `.game-install.lock` impede duas instalações simultâneas na mesma raiz. Se o processo for encerrado abruptamente, confirme que não está mais rodando antes de remover esse bloqueio.

## Ports PS2 sem ISO

`layout.py` gera apenas um índice de extensões e pequenos metadados de diretório em `game_files/.disc/`. O conteúdo continua em arquivos comuns, sem reconstruir uma ISO nem manter um pacote com o conteúdo completo. Importações de ISO preservam os LBNs dos arquivos; pastas recebem um layout determinístico. Recursos que tenham LBNs originais codificados internamente exigem teste específico quando importados de pasta.

`runtime/extracted_files.cmake` gera cópias locais dos pontos de acesso CD do PS2Recomp, incluindo leituras nativas do EE e IOP. Inclua depois das adaptações específicas do jogo e recompile o runtime. O checkout compartilhado não é alterado. Builds antigos precisam ser recompilados; instalar dados não modifica um executável antigo.

## Desenvolvimento e distribuição

```sh
python -m unittest discover -s tests -v
python -m PyInstaller --onefile --windowed --name Instalar-Arquivos installer.py
```

Coloque `Instalar-Arquivos.exe` ao lado do `installer.json` de cada port. O workflow produz o executável Windows; o usuário final não precisa instalar Python ao usar esse pacote. Os submódulos fixam a revisão para manter builds reproduzíveis. Atualizações são explícitas.

Nenhum arquivo de jogo, ISO, chave, binário original ou código recompilado é distribuído neste repositório. O instalador prepara os dados; compilação, compatibilidade e execução do port são etapas próprias de cada projeto.
