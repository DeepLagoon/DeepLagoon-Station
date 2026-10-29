#!/usr/bin/env python3
"""Add /art-tablet/ to an existing nginx HTTPS site. Python 3.9+, no packages.

sudo python3 install_tablet_nginx.py
sudo python3 install_tablet_nginx.py --dry-run
sudo python3 install_tablet_nginx.py --upstream http://GAME_HOST:1212
"""

import argparse
import difflib
import glob
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass

ROUTE = "/art-tablet/"
BEGIN = "# BEGIN DeepLagoon art tablet (managed)"
END = "# END DeepLagoon art tablet (managed)"
BACKUPS = Path("/var/backups/deeplagoon-art-tablet")


class InstallError(Exception):
    pass


@dataclass
class Token:
    value: str
    start: int
    end: int
    symbol: bool = False


@dataclass
class Directive:
    words: list
    start: int
    end: int
    brace: int = -1
    close: int = -1
    children: list = None
    source: object = None


def tokenize(text):
    """Read nginx words, quotes, escapes, variables and comments with source spans."""
    result, i = [], 0
    while i < len(text):
        if text[i].isspace():
            i += 1
            continue
        if text[i] == "#":
            newline = text.find("\n", i)
            i = len(text) if newline < 0 else newline + 1
            continue
        start = i
        if text[i] in "{};":
            result.append(Token(text[i], i, i + 1, True))
            i += 1
            continue
        word, quote = [], None
        while i < len(text):
            char = text[i]
            if char == "\\" and i + 1 < len(text):
                word.append(text[i + 1])
                i += 2
                continue
            if quote:
                if char == quote:
                    quote = None
                else:
                    word.append(char)
                i += 1
                continue
            if char in "\"'":
                quote = char
                i += 1
                continue
            if text.startswith("${", i):
                end = text.find("}", i + 2)
                if end < 0:
                    raise InstallError("Незакрытая переменная nginx.")
                word.append(text[i:end + 1])
                i = end + 1
                continue
            if char.isspace() or char in "{};#":
                break
            word.append(char)
            i += 1
        if quote:
            raise InstallError("Незакрытая строка nginx.")
        result.append(Token("".join(word), start, i))
    return result


def parse(text):
    tokens = tokenize(text)
    position = 0

    def scope(nested=False):
        nonlocal position
        nodes = []
        while position < len(tokens):
            if tokens[position].symbol and tokens[position].value == "}":
                if not nested:
                    raise InstallError("Неожиданная закрывающая скобка nginx.")
                close = tokens[position]
                position += 1
                return nodes, close
            start, words = tokens[position].start, []
            while position < len(tokens) and not tokens[position].symbol:
                words.append(tokens[position].value)
                position += 1
            if not words or position >= len(tokens) or tokens[position].symbol and tokens[position].value == "}":
                raise InstallError("Не удалось разобрать директиву nginx.")
            terminal = tokens[position]
            position += 1
            if terminal.value == ";":
                nodes.append(Directive(words, start, terminal.end))
            else:
                children, close = scope(True)
                nodes.append(Directive(words, start, close.end, terminal.start, close.start, children))
        if nested:
            raise InstallError("Незакрытый блок nginx.")
        return nodes, None

    return scope()[0]


def validate_domain(domain):
    if len(domain) > 253 or not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", domain):
        raise InstallError("Домен должен быть DNS-именем без схемы, порта и пути.")
    if any(not label or len(label) > 63 or label.startswith("-") or label.endswith("-") for label in domain.split(".")):
        raise InstallError("Некорректное DNS-имя.")
    return domain.lower()


def validate_upstream(value):
    if not re.fullmatch(r"http://(?:[A-Za-z0-9.-]+|\[[0-9A-Fa-f:]+\]):[0-9]{1,5}", value):
        raise InstallError("--upstream: нужен адрес вида http://127.0.0.1:1212 без завершающего / и без пароля.")
    parsed = urllib.parse.urlsplit(value)
    if not 1 <= parsed.port <= 65535:
        raise InstallError("Порт upstream должен находиться в диапазоне 1–65535.")
    return value


def run(command):
    try:
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise InstallError(f"Не удалось выполнить {command[0]}: {error}") from error
    if result.returncode:
        raise InstallError(f"Ошибка команды {' '.join(command)}:\n{result.stderr.strip()}")
    return result.stdout


def loaded_files(dump):
    names = re.findall(r"^# configuration file (.+):\s*$", dump, re.M)
    if not names:
        raise InstallError("nginx -T не вернул список конфигурационных файлов.")
    paths = list(dict.fromkeys(Path(name).resolve(strict=True) for name in names))
    files = {path: path.read_bytes() for path in paths}
    parsed = {path: parse(content.decode("utf-8")) for path, content in files.items()}
    def assign(nodes, path):
        for node in nodes:
            node.source = path
            assign(node.children or [], path)
    for path, nodes in parsed.items():
        assign(nodes, path)
    return files, parsed, Path(names[0]).absolute().parent


def expanded(nodes, parsed, prefix, stack=()):
    for node in nodes:
        if node.words[0] != "include":
            yield node
            continue
        if len(node.words) != 2 or "$" in node.words[1]:
            raise InstallError("Не поддерживается динамический include в HTTPS-блоке.")
        pattern = Path(node.words[1])
        if not pattern.is_absolute():
            pattern = prefix / pattern
        for name in sorted(glob.glob(str(pattern))):
            path = Path(name).resolve(strict=True)
            if path in stack or len(stack) >= 32 or path not in parsed:
                raise InstallError(f"Не удалось безопасно раскрыть include: {path}")
            yield from expanded(parsed[path], parsed, prefix, stack + (path,))


def servers(nodes, parsed, prefix, context=()):
    for node in expanded(nodes, parsed, prefix):
        if node.words == ["server"] and context == ("http",):
            yield node
        elif node.children is not None:
            yield from servers(node.children, parsed, prefix, context + (node.words[0],))


def tls_site(node, domain, parsed, prefix):
    children = list(expanded(node.children, parsed, prefix))
    names = [name.lower() for child in children if child.words[0] == "server_name" for name in child.words[1:]]
    tls = any(child.words[0] == "listen" and "ssl" in child.words[2:] and
              (child.words[1] == "443" or child.words[1].endswith(":443")) for child in children if len(child.words) >= 2)
    return domain in names and tls, children


def patch_server(text, node, children, upstream):
    body = text[node.brace + 1:node.close]
    starts = [m.start() for m in re.finditer(re.escape(BEGIN), body)]
    ends = [m.end() for m in re.finditer(re.escape(END), body)]
    if len(starts) != len(ends) or len(starts) > 1 or starts and starts[0] >= ends[0]:
        raise InstallError("Повреждены маркеры ранее установленного маршрута.")
    owned = (node.brace + 1 + starts[0], node.brace + 1 + ends[0]) if starts else None
    for child in children:
        if child.words[0] == "location" and any(ROUTE.rstrip("/") in word for word in child.words[1:]):
            if child.source != node.source or not owned or not (owned[0] <= child.start and child.end <= owned[1]):
                raise InstallError("Уже есть отдельный location для /art-tablet/. Он не создан этим скриптом; автоматически перезаписывать его нельзя.")
    indent = re.match(r"[ \t]*", text[text.rfind("\n", 0, node.start) + 1:]).group() + "    "
    lines = [BEGIN, "location ^~ /art-tablet/ {", f"    proxy_pass {upstream};",
             "    proxy_set_header Host $host;", "    proxy_set_header Authorization $http_authorization;",
             "    proxy_set_header Origin $http_origin;", "    proxy_set_header If-Match $http_if_match;",
             "    proxy_set_header X-Art-Draft-Writer $http_x_art_draft_writer;",
             "    proxy_set_header X-Art-Draft-Sequence $http_x_art_draft_sequence;",
             "    proxy_set_header X-Art-Draft-Epoch $http_x_art_draft_epoch;",
             "    proxy_set_header X-Art-Draft-Version $http_x_art_draft_version;",
             "    proxy_pass_request_headers on;", "    proxy_intercept_errors off;", "    proxy_cache off;",
             "    client_max_body_size 9m;", "    proxy_read_timeout 130s;", "    proxy_send_timeout 130s;", "}", END]
    snippet = "\n".join(indent + line for line in lines)
    if owned:
        start = text.rfind("\n", 0, owned[0]) + 1
        if text[start:owned[0]].strip():
            raise InstallError("Маркер маршрута должен стоять на отдельной строке.")
        return start, owned[1], snippet
    insertion = text.rfind("\n", node.brace, node.close) + 1
    if insertion <= node.brace or text[insertion:node.close].strip():
        return node.close, node.close, "\n" + snippet + "\n" + indent[:-4]
    return insertion, insertion, snippet + "\n"


def plan(dump, domain, upstream, site_config=None):
    files, parsed, prefix = loaded_files(dump)
    edits, count = {}, 0
    for node in servers(parsed[next(iter(parsed))], parsed, prefix):
        path = node.source
        if site_config and path != site_config.resolve(strict=True):
            continue
        text = files[path].decode("utf-8")
        match, children = tls_site(node, domain, parsed, prefix)
        if match:
            edits.setdefault(path, []).append(patch_server(text, node, children, upstream))
            count += 1
    if not count:
        raise InstallError(f"Не найден HTTPS-блок с точным server_name {domain} и listen 443 ssl. Укажите --site-config, если нужен конкретный загруженный файл.")
    changes = {}
    for path, replacements in edits.items():
        text = files[path].decode("utf-8")
        for start, end, replacement in sorted(replacements, reverse=True):
            text = text[:start] + replacement + text[end:]
        content = text.encode("utf-8")
        if content != files[path]:
            changes[path] = (files[path], content)
    return changes, count


def probe(base):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    for suffix in ("", "tgui.bundle.js", "tgui.bundle.css"):
        url = base + ROUTE + suffix
        try:
            with opener.open(url, timeout=10) as response:
                content = response.read(4096)
                if response.status != 200 or not suffix and b'data-art-tablet="true"' not in content:
                    raise InstallError(f"{url}: ответ не является редактором планшета.")
        except (OSError, urllib.error.URLError) as error:
            raise InstallError(f"{url}: {error}") from error


def atomic_write(path, content):
    info = path.stat()
    descriptor, name = tempfile.mkstemp(prefix=".art-tablet-", dir=path.parent)
    temporary = Path(name)
    try:
        os.close(descriptor)
        shutil.copy2(path, temporary)
        with temporary.open("wb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.chown(temporary, info.st_uid, info.st_gid)
        os.chmod(temporary, info.st_mode & 0o7777)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def install(changes, nginx, check, backup_root=BACKUPS):
    backup_root.mkdir(mode=0o700, parents=True, exist_ok=True)
    backup = Path(tempfile.mkdtemp(prefix=time.strftime("%Y%m%d-%H%M%S-"), dir=backup_root))
    records = []
    for index, (path, (before, after)) in enumerate(changes.items()):
        if path.read_bytes() != before or path.stat().st_nlink != 1:
            raise InstallError(f"Файл изменился или имеет несколько жёстких ссылок: {path}")
        saved = backup / f"{index:02d}-{path.name}"
        shutil.copy2(path, saved)
        records.append({"path": str(path), "backup": saved.name,
                        "before_sha256": hashlib.sha256(before).hexdigest(), "after_sha256": hashlib.sha256(after).hexdigest()})
    (backup / "manifest.json").write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Резервная копия: {backup}", flush=True)
    written, reload_attempted = [], False
    try:
        for path, (before, after) in changes.items():
            if path.read_bytes() != before:
                raise InstallError(f"Конфигурация изменена другим процессом: {path}")
            written.append(path)
            atomic_write(path, after)
        run(nginx + ["-t"])
        reload_attempted = True
        run(nginx + ["-s", "reload"])
        last_error = None
        for attempt in range(5):
            try:
                check()
                return backup
            except InstallError as error:
                last_error = error
                time.sleep(1)
        raise last_error
    except BaseException:
        failures = []
        for path in reversed(written):
            before, after = changes[path]
            try:
                current = path.read_bytes()
                if current == before:
                    continue
                if current != after:
                    raise InstallError("файл изменён другим процессом; автоматическая перезапись отменена")
                atomic_write(path, before)
            except Exception as error:
                failures.append(f"{path}: {error}")
        if written and not failures:
            try:
                run(nginx + ["-t"])
                if reload_attempted:
                    run(nginx + ["-s", "reload"])
                print("Исходная конфигурация восстановлена.", file=sys.stderr)
            except InstallError as error:
                failures.append(str(error))
        if failures:
            print("Нужен ручной откат из резервной копии:\n" + "\n".join(failures), file=sys.stderr)
        raise


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--domain", default="deep-lagoon-ss14.ru")
    parser.add_argument("--upstream", default="http://127.0.0.1:1212", help="HTTP/status-адрес игрового сервера, без завершающего /")
    parser.add_argument("--site-config", type=Path, help="Ограничить поиск одним уже загруженным конфигурационным файлом")
    parser.add_argument("--nginx-conf", type=Path, help="Основной nginx.conf, если nginx запущен с -c")
    parser.add_argument("--dry-run", action="store_true", help="Показать изменения без записи и reload")
    args = parser.parse_args(argv)
    domain, upstream = validate_domain(args.domain), validate_upstream(args.upstream)
    if sys.platform != "linux" or os.geteuid() != 0:
        raise InstallError("Запустите на Linux-хосте nginx: sudo python3 install_tablet_nginx.py")
    import fcntl
    binary = shutil.which("nginx")
    if not binary:
        raise InstallError("nginx не найден. Скрипт настраивает уже установленный nginx с действующим HTTPS-сертификатом.")
    nginx = [binary]
    if args.nginx_conf:
        nginx += ["-c", str(args.nginx_conf.resolve(strict=True))]
    with open("/run/lock/deeplagoon-art-tablet.lock", "a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise InstallError("Другой экземпляр установщика уже работает.") from error
        dump = run(nginx + ["-T"])
        changes, count = plan(dump, domain, upstream, args.site_config)
        print(f"Найдено HTTPS-блоков: {count}; upstream: {upstream}")
        if args.dry_run:
            for path, (before, after) in changes.items():
                sys.stdout.writelines(difflib.unified_diff(before.decode("utf-8").splitlines(True), after.decode("utf-8").splitlines(True), fromfile=str(path), tofile=str(path)))
            print("Проверочный запуск: файлы не записаны, reload не выполнен.")
            return 0
        try:
            probe(upstream)
        except InstallError as error:
            raise InstallError(f"Игровой редактор не доступен по --upstream. Если он на другом хосте, укажите его HTTP/status-адрес.\n{error}") from error
        if changes:
            install(changes, nginx, lambda: probe("https://" + domain))
        else:
            probe("https://" + domain)
            print("Маршрут уже настроен; повторная запись не требуется.")
        print(f"Готово: https://{domain}{ROUTE}\nВ конфиге игры:\n[art]\ntablet_url = \"https://{domain}{ROUTE}\"\nЗаново нажмите «Открыть для планшета», чтобы получить свежую ссылку.")
        return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (InstallError, OSError, UnicodeError) as error:
        print(f"ОШИБКА: {error}", file=sys.stderr)
        sys.exit(1)
    except KeyboardInterrupt:
        print("Прервано пользователем.", file=sys.stderr)
        sys.exit(130)
