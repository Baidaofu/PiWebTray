"""极简 CONNECT 隧道：本地 DNS 解析到的 github.com IP 不可达时，指定可达 IP 转发。

用法：
    python tools/connect_proxy.py [本地端口] [上游IP] [上游端口]
    git -c http.proxy=http://127.0.0.1:8899 push

仅用于本机网络受限时的临时推送，GitHub 端证书照常校验（字节透传）。
"""
import socket
import sys
import threading
import select

LOCAL_PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8899
UP_HOST = sys.argv[2] if len(sys.argv) > 2 else "140.82.112.3"
UP_PORT = int(sys.argv[3]) if len(sys.argv) > 3 else 443
CRLF = b"\r\n\r\n"


def pipe(a, b):
    try:
        while True:
            r, _, _ = select.select([a, b], [], [], 120)
            if not r:
                break
            for s in r:
                data = s.recv(65536)
                if not data:
                    return
                (b if s is a else a).sendall(data)
    except Exception:
        pass
    finally:
        for s in (a, b):
            try:
                s.close()
            except Exception:
                pass


def handle(c):
    try:
        buf = b""
        while CRLF not in buf:
            d = c.recv(4096)
            if not d:
                return
            buf += d
        head = buf.split(CRLF)[0].decode("latin-1", "replace")
        if not head.upper().startswith("CONNECT"):
            c.sendall(b"HTTP/1.1 405 Method Not Allowed\r\n\r\n")
            c.close()
            return
        # 自己应答 CONNECT，再与上游建立裸隧道
        c.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
        u = socket.create_connection((UP_HOST, UP_PORT), 10)
        rest = buf.split(CRLF, 1)[1] if CRLF in buf else b""
        if rest:
            u.sendall(rest)   # 客户端可能已把 TLS ClientHello 一起发来，不能丢
        pipe(c, u)
    except Exception:
        try:
            c.close()
        except Exception:
            pass


def main():
    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", LOCAL_PORT))
    srv.listen(64)
    print("CONNECT proxy 127.0.0.1:%d -> %s:%d" % (LOCAL_PORT, UP_HOST, UP_PORT), flush=True)
    while True:
        c, _ = srv.accept()
        threading.Thread(target=handle, args=(c,), daemon=True).start()


if __name__ == "__main__":
    main()