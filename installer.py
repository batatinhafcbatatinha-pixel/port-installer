"""Offline game-data installer. Python 3.11+, standard library only."""
import argparse
from datetime import datetime, timedelta
import json
from pathlib import Path
import queue
import sys
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox, scrolledtext, ttk

from engine import Cancelled, delete_source, install


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', type=Path, default=(Path(sys.executable).parent if getattr(sys, 'frozen', False) else Path.cwd()) / 'installer.json')
    parser.add_argument('--root', type=Path)
    args = parser.parse_args()
    if not args.config.is_file():
        parser.error('Configuração installer.json não encontrada ao lado do instalador')
    config = json.loads(args.config.read_text(encoding='utf-8-sig'))
    root = (args.root or args.config.resolve().parent).resolve()
    app = tk.Tk()
    app.title('Instalar arquivos — ' + config['name'])
    app.geometry('1040x650')
    app.minsize(900, 600)
    # Quiet, editorial layout: dark navigation rail, warm paper workspace, single green accent.
    paper, ink, muted, line, accent, rail = '#f4f3ee', '#202622', '#667069', '#d9ddd5', '#286448', '#18241e'
    app.configure(bg=paper)
    style = ttk.Style(app)
    style.theme_use('clam')
    style.configure('.', font=('Segoe UI', 10), background=paper, foreground=ink)
    style.configure('TFrame', background=paper)
    style.configure('TLabel', background=paper, foreground=ink)
    style.configure('Muted.TLabel', foreground=muted)
    style.configure('TEntry', fieldbackground='#ffffff', bordercolor=line, padding=9)
    style.configure('TButton', background=paper, foreground=ink, bordercolor=line, padding=(14, 9), relief='flat')
    style.map('TButton', background=[('active', '#e5e8e0')], foreground=[('disabled', '#9ba29b')])
    style.configure('Primary.TButton', background=accent, foreground='#ffffff', bordercolor=accent)
    style.map('Primary.TButton', background=[('active', '#1e5038'), ('disabled', '#bac7bb')])
    style.configure('TProgressbar', troughcolor='#e1e4dc', background=accent, bordercolor=paper, lightcolor=accent, darkcolor=accent, thickness=6)
    sidebar = tk.Frame(app, bg=rail, width=190)
    sidebar.pack(side='left', fill='y')
    sidebar.pack_propagate(False)
    tk.Label(sidebar, text='PORT / INSTALL', bg=rail, fg='#9fb6a3', font=('Segoe UI', 10, 'bold')).pack(anchor='w', padx=24, pady=(30, 38))
    tk.Label(sidebar, text='Instalação\nlocal.', justify='left', bg=rail, fg='#f4f3ee', font=('Segoe UI', 23, 'bold')).pack(anchor='w', padx=24)
    tk.Label(sidebar, text='01   ESCOLHER\n\n02   INSTALAR\n\n03   VERIFICAR', justify='left', bg=rail, fg='#9fb6a3', font=('Segoe UI', 10)).pack(anchor='w', padx=24, pady=38)
    tk.Label(sidebar, text='ARQUIVOS LOCAIS\nNenhum jogo é baixado.', justify='left', bg=rail, fg='#9fb6a3', font=('Segoe UI', 9)).pack(side='bottom', anchor='w', padx=24, pady=26)
    body = ttk.Frame(app, padding=(30, 28))
    body.pack(fill='both', expand=True)
    ttk.Label(body, text='INSTALAÇÃO DE ARQUIVOS', style='Muted.TLabel', font=('Segoe UI', 9, 'bold')).pack(anchor='w')
    ttk.Label(body, text=config['name'], font=('Segoe UI', 23, 'bold'), wraplength=610).pack(anchor='w', pady=(6, 2))
    ttk.Label(body, text=config.get('revision', ''), style='Muted.TLabel').pack(anchor='w', pady=(0, 22))
    ttk.Label(body, text='De onde vêm os arquivos?', font=('Segoe UI', 12, 'bold')).pack(anchor='w')
    ttk.Label(body, text='Escolha sua ISO ou a pasta completa do jogo.', style='Muted.TLabel').pack(anchor='w', pady=(4, 10))
    source = tk.StringVar()
    row = ttk.Frame(body)
    row.pack(fill='x')
    entry = ttk.Entry(row, textvariable=source)
    entry.pack(side='left', fill='x', expand=True)
    def choose_iso():
        value = filedialog.askopenfilename(filetypes=[('Imagem ISO', '*.iso'), ('Todos os arquivos', '*.*')])
        if value:
            source.set(value)
    def choose_folder():
        value = filedialog.askdirectory()
        if value:
            source.set(value)
    iso_button = ttk.Button(row, text='Escolher ISO…', command=choose_iso)
    iso_button.pack(side='left', padx=6)
    folder_button = ttk.Button(row, text='Escolher pasta…', command=choose_folder)
    folder_button.pack(side='left')
    ttk.Label(body, style='Muted.TLabel', text='Destino: ' + str(root / config.get('destination', 'game_files')), wraplength=780).pack(anchor='w', pady=10)
    status = tk.StringVar(value='Pronto para instalar')
    ttk.Label(body, textvariable=status, wraplength=780).pack(anchor='w')
    progress = ttk.Progressbar(body, maximum=100)
    progress.pack(fill='x', pady=8)
    timing = tk.StringVar(value='A estimativa será calculada durante a cópia e verificação.')
    ttk.Label(body, textvariable=timing).pack(anchor='w', pady=(0, 10))
    ttk.Label(body, text='ATIVIDADE', style='Muted.TLabel', font=('Segoe UI', 9, 'bold')).pack(anchor='w', pady=(8, 8))
    log = scrolledtext.ScrolledText(body, height=10, state='disabled', wrap='word', font=('Consolas', 9), bg='#eaece5', fg='#45564a', relief='flat', borderwidth=0, padx=14, pady=12, insertbackground=ink)
    log.pack(fill='both', expand=True)
    controls = ttk.Frame(body)
    controls.pack(fill='x', pady=(12, 0))
    events, cancel = queue.Queue(), threading.Event()
    running, started, latest, last_emit = False, 0, None, 0

    def emit(event):
        nonlocal latest, last_emit
        if event['type'] == 'progress':
            now = time.monotonic()
            if now - last_emit < .1 and event['done'] != event['total']:
                return
            last_emit = now
        events.put(event)

    def append(text):
        log.configure(state='normal')
        log.insert('end', text + '\n')
        # Keep the live widget bounded even for discs with thousands of files.
        lines = int(log.index('end-1c').split('.')[0])
        if lines > 2000:
            log.delete('1.0', f'{lines-1800}.0')
        log.see('end')
        log.configure(state='disabled')

    def worker(path):
        try:
            result = install(path, root, config, emit, cancel)
            events.put(dict(type='complete', result=result))
        except Cancelled as exc:
            events.put(dict(type='cancelled', text=str(exc)))
        except Exception as exc:
            events.put(dict(type='error', text=str(exc)))

    def set_busy(value):
        nonlocal running
        running = value
        for widget in (entry, iso_button, folder_button, start_button):
            widget.configure(state='disabled' if value else 'normal')
        cancel_button.configure(state='normal' if value else 'disabled')

    def start():
        nonlocal started, latest
        if not source.get().strip():
            messagebox.showinfo('Escolha a origem', 'Selecione a ISO ou a pasta completa do jogo.')
            return
        cancel.clear()
        started, latest = time.monotonic(), None
        progress.configure(mode='indeterminate')
        progress.start(15)
        set_busy(True)
        append('Iniciando instalação…')
        threading.Thread(target=worker, args=(source.get(),), daemon=True).start()

    start_button = ttk.Button(controls, text='Instalar arquivos  →', style='Primary.TButton', command=start)
    start_button.pack(side='right')
    cancel_button = ttk.Button(controls, text='Cancelar', state='disabled', command=cancel.set)
    cancel_button.pack(side='right', padx=8)

    def poll():
        nonlocal latest
        for _ in range(100):
            try:
                event = events.get_nowait()
            except queue.Empty:
                break
            kind = event['type']
            if kind == 'status':
                status.set(event['text'])
                append(event['text'])
            elif kind == 'progress':
                latest = event
                progress.stop()
                progress.configure(mode='determinate', value=100 * event['done'] / max(event['total'], 1))
            else:
                progress.stop()
                set_busy(False)
                if kind == 'complete':
                    progress.configure(mode='determinate', value=100)
                    status.set('Arquivos instalados e verificados')
                    append('Instalação concluída. Relatório: game-install-report.json')
                    result = event['result']
                    if result['canDeleteSource'] and messagebox.askyesno('Apagar a ISO original?',
                        'Todos os arquivos foram verificados.' + (' O jogo usará os arquivos extraídos, sem precisar da ISO.') +
                        '\n\nQuer apagar permanentemente esta ISO original?\n' + result['source'], default='no'):
                        try:
                            delete_source(result)
                            append('ISO original apagada por sua confirmação.')
                        except Exception as exc:
                            messagebox.showerror('Não foi possível apagar', str(exc))
                    else:
                        append('Origem preservada.')
                else:
                    status.set(event['text'])
                    append(event['text'])
                    if kind == 'error':
                        messagebox.showerror('Instalação não concluída', event['text'])
        if started:
            elapsed = time.monotonic() - started
            if running and latest and latest['done'] > 0:
                remaining = elapsed * (latest['total'] - latest['done']) / latest['done']
                finish = datetime.now() + timedelta(seconds=remaining)
                timing.set(f'{latest["done"] / max(latest["total"], 1):.0%} • Decorrido: {int(elapsed)} s • Restam ~{int(remaining)} s • Término estimado: {finish:%H:%M:%S}')
            elif running:
                timing.set(f'Decorrido: {int(elapsed)} s • Analisando origem; estimativa ainda indisponível')
        app.after(100, poll)

    def close():
        if running:
            cancel.set()
            status.set('Cancelando… aguarde antes de fechar')
        else:
            app.destroy()
    app.protocol('WM_DELETE_WINDOW', close)
    app.after(100, poll)
    app.mainloop()


if __name__ == '__main__':
    main()
