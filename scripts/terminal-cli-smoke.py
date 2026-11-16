#!/usr/bin/env python3
"""Optional Linux CLI integration test. Build Release first; requires headless Vulkan, not SDL or a display server."""
import fcntl
import os
from pathlib import Path
import pty
import re
import select
import signal
import struct
import subprocess
import tempfile
import termios
import time

REPO = Path(__file__).resolve().parent.parent
PROJECT = REPO / 'samples/Spinners/Game/Spinners.Game.csproj'
COMMAND = ['dotnet', str(REPO / 'src/Gamegrl.Editor/bin/Release/net10.0/Gamegrl.Editor.dll')]


def screen(data, width, height):
    rows = [[' '] * width for _ in range(height)]
    x = y = 0
    text = data.decode('utf-8', errors='replace')
    i = 0
    while i < len(text):
        c = text[i]
        if c == '\x1b':
            match = re.match(r'\x1b\[([0-9;?]*)([A-Za-z])', text[i:])
            if match:
                raw, op = match.groups()
                args = [int(v or '0') for v in raw.lstrip('?').split(';')]
                n = args[0] or 1
                if not raw.startswith('?'):
                    if op in ('H', 'f'): y, x = n - 1, (args[1] or 1) - 1 if len(args) > 1 else 0
                    elif op == 'A': y -= n
                    elif op == 'B': y += n
                    elif op == 'C': x += n
                    elif op == 'D': x -= n
                    elif op == 'G': x = n - 1
                    elif op == 'J' and args[0] == 2: rows = [[' '] * width for _ in range(height)]
                    elif op == 'K' and 0 <= y < height:
                        for col in range(max(0, x), width): rows[y][col] = ' '
                i += len(match.group()); continue
            if text[i:i+2] == '\x1b]':
                end = text.find('\x07', i + 2)
                if end < 0:
                    end = text.find('\x1b\\', i + 2)
                    if end < 0: break
                    i = end + 2
                else: i = end + 1
                continue
            i += 2; continue
        if c == '\r': x = 0
        elif c == '\n': y += 1
        elif c == '\b': x = max(0, x - 1)
        elif c >= ' ':
            if 0 <= x < width and 0 <= y < height: rows[y][x] = c
            x += 1
        x = max(0, x); y = max(0, y)
        i += 1
    return [''.join(row) for row in rows]


def wait_for(read, predicate, process, seconds=35):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        data = read()
        if predicate(data): return data
        if process.poll() is not None:
            raise AssertionError(f'Editor exited unexpectedly: {process.returncode}')
        time.sleep(.05)
    raise AssertionError('Terminal screen did not reach the expected state')


def click(write, x, y):
    write(f'\x1b[<0;{x+1};{y+1}M'.encode())
    time.sleep(.08)
    write(f'\x1b[<0;{x+1};{y+1}m'.encode())
    time.sleep(.08)


def main():
    os.chdir(REPO)
    with tempfile.TemporaryDirectory(prefix='gamegrl-cli-smoke-') as temporary:
        root = Path(temporary)
        (root / 'Assets').mkdir()
        for source in (REPO / 'samples/Spinners/Assets').iterdir():
            if source.is_file(): (root / 'Assets' / source.name).write_bytes(source.read_bytes())
        original = (root / 'Assets/main.scene').read_bytes()
        # Pipe transport: no TTY, DISPLAY or font; monochrome ASCII fallback and EOF recovery.
        environment = dict(os.environ, GAMEGRL_FONT='/no-font-required', DISPLAY='', WAYLAND_DISPLAY='')
        for recover in (False, True):
            output = root / ('recover.ansi' if recover else 'pipe.ansi')
            with output.open('wb') as sink:
                process = subprocess.Popen(COMMAND + [str(root), str(PROJECT), '--stdio', '--columns', '100', '--rows', '32', '--colors', 'none', '--ascii'], stdin=subprocess.PIPE, stdout=sink, stderr=subprocess.PIPE, env=environment)
                try:
                    read = output.read_bytes
                    wait_for(read, lambda b: screen(b, 100, 32)[0].startswith('Assets/main.scene'), process)
                    if recover:
                        process.stdin.write(b'\x10'); process.stdin.flush()  # Ctrl+P opens the command palette.
                        wait_for(read, lambda b: any('File: Save' in row for row in screen(b, 100, 32)), process)
                        process.stdin.write(b'recover'); process.stdin.flush()
                        wait_for(read, lambda b: any('Recover disconnected scene' in row for row in screen(b, 100, 32)), process)
                        process.stdin.write(b'\r'); process.stdin.flush()
                    else:
                        process.stdin.write(b'\x0e'); process.stdin.flush()  # Ctrl+N.
                    wait_for(read, lambda b: screen(b, 100, 32)[0].startswith('Untitled *'), process)
                    if recover:
                        process.stdin.write(b'\x11'); process.stdin.flush()  # Ctrl+Q.
                        wait_for(read, lambda b: any('Unsaved scene' in row for row in screen(b, 100, 32)), process)
                        click(lambda b: (process.stdin.write(b), process.stdin.flush()), 50, 12)  # Discard.
                    else:
                        process.stdin.close()  # EOF must save the dirty document and exit.
                    process.wait(timeout=15)
                    assert process.returncode == 0, process.stderr.read().decode()
                    assert b'\x1b[?1049l' in read(), 'Alternate screen was not restored'
                    assert (root / 'Assets/main.scene').read_bytes() == original, 'Source scene was overwritten'
                    assert list((root / 'Library/recovery').glob('*.scene')), 'Disconnect recovery is missing'
                finally:
                    if process.poll() is None: process.kill(); process.wait()
        # Real PTY: raw mode, truecolor, SIGWINCH size and SIGHUP recovery/restoration.
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 100, 0, 0))
        before = termios.tcgetattr(slave)
        data = bytearray()
        def read_pty():
            while select.select([master], [], [], 0)[0]:
                try: data.extend(os.read(master, 65536))
                except OSError: break
            return bytes(data)
        environment.update(TERM='xterm-256color', COLORTERM='truecolor')
        process = subprocess.Popen(COMMAND + [str(root), str(PROJECT), '--terminal'], stdin=slave, stdout=slave, stderr=subprocess.PIPE, env=environment)
        try:
            wait_for(read_pty, lambda b: screen(b, 100, 24)[0].startswith('Assets/main.scene'), process)
            fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 30, 110, 0, 0))
            process.send_signal(signal.SIGWINCH)
            wait_for(read_pty, lambda b: screen(b, 110, 30)[0].startswith('Assets/main.scene'), process)
            os.write(master, b'\x0e')
            wait_for(read_pty, lambda b: screen(b, 110, 30)[0].startswith('Untitled ●'), process)
            process.send_signal(signal.SIGHUP)
            until = time.monotonic() + 15
            while process.poll() is None and time.monotonic() < until: read_pty(); time.sleep(.02)
            assert process.wait(timeout=2) == 0, process.stderr.read().decode()
            after = termios.tcgetattr(slave)
            assert before[3] == after[3], 'TTY local modes were not restored'
            assert len(list((root / 'Library/recovery').glob('*.scene'))) >= 2, 'SIGHUP recovery is missing'
        finally:
            if process.poll() is None: process.kill(); process.wait()
            os.close(master); os.close(slave)
    print('Terminal CLI smoke passed: pipe transport, ASCII/monochrome, EOF recovery, recovery command, clean close, PTY resize, SIGHUP and raw-mode restoration.')


if __name__ == '__main__':
    main()
