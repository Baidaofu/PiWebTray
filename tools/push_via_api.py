"""通过 GitHub Git Data API 推送本地提交（用于 github.com 主站不可达的场景）。

用法：
    python tools/push_via_api.py            # 推送当前 HEAD 到 origin/main
    python tools/push_via_api.py --dry-run  # 只打印将上传的文件

凭据取自 Git Credential Manager：
    printf 'protocol=https\\nhost=github.com\\n\\n' | git credential fill
"""
import base64
import json
import os
import subprocess
import sys

API = "https://api.github.com"


def get_cred():
    out = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True, check=True,
    ).stdout
    cred = {}
    for line in out.splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            cred[k] = v
    return cred["username"], cred["password"]


class Api:
    def __init__(self, token):
        self.token = token

    def call(self, method, path, data=None):
        args = ["curl", "-s", "-m", "60", "-X", method,
                "-H", "Authorization: Bearer " + self.token,
                "-H", "Accept: application/vnd.github+json",
                "-H", "X-GitHub-Api-Version: 2022-11-28"]
        if data is not None:
            args += ["-d", json.dumps(data)]
        args.append(API + path)
        raw = subprocess.run(args, capture_output=True, text=True).stdout
        obj = json.loads(raw) if raw.strip() else {}
        if isinstance(obj, dict) and obj.get("message") and "url" not in obj and "sha" not in obj:
            raise RuntimeError("%s %s -> %s: %s" % (method, path, obj.get("status"), obj.get("message")))
        return obj

    def blob(self, data: bytes):
        return self.call("POST", "/repos/%s/%s/git/blobs" % (OWNER, REPO),
                         {"content": base64.b64encode(data).decode(), "encoding": "base64"})["sha"]

    def tree(self, entries):
        return self.call("POST", "/repos/%s/%s/git/trees" % (OWNER, REPO),
                         {"base_tree": "main" if has_main() else None, "tree": entries})["sha"]


def has_main():
    return ref_cache is not None


ref_cache = None


def main():
    global OWNER, REPO, ref_cache
    dry = "--dry-run" in sys.argv

    remote = subprocess.run(["git", "remote", "get-url", "origin"], capture_output=True, text=True).stdout.strip()
    OWNER, REPO = remote.rstrip("/").removesuffix(".git").split("/")[-2:]

    files = subprocess.run(["git", "ls-files"], capture_output=True, text=True, check=True).stdout.split()
    user, token = get_cred()
    api = Api(token)

    try:
        ref_cache = api.call("GET", "/repos/%s/%s/git/ref/heads/main" % (OWNER, REPO))
        parent = ref_cache["object"]["sha"]
        print("remote main =", parent[:10])
    except RuntimeError:
        ref_cache = None
        parent = None
        print("remote main not found, creating new branch")

    entries = []
    for f in files:
        with open(f, "rb") as fh:
            data = fh.read()
        mode = "100755" if os.access(f, os.X_OK) and f.endswith((".sh", ".py")) else "100644"
        sha = api.blob(data)
        entries.append({"path": f, "mode": mode, "type": "blob", "sha": sha})
        print("  blob %-24s %7d bytes  %s" % (f, len(data), sha[:10]))

    msg = subprocess.run(["git", "log", "-1", "--pretty=%s%n%b"], capture_output=True, text=True).stdout.strip()
    if dry:
        return

    tree = api.tree(entries)
    commit = api.call("POST", "/repos/%s/%s/git/commits" % (OWNER, REPO),
                      {"message": msg, "tree": tree, "parents": [parent] if parent else []})
    if parent:
        api.call("PATCH", "/repos/%s/%s/git/refs/heads/main" % (OWNER, REPO),
                 {"sha": commit["sha"], "force": False})
    else:
        api.call("POST", "/repos/%s/%s/git/refs" % (OWNER, REPO),
                 {"ref": "refs/heads/main", "sha": commit["sha"]})
    print("pushed commit", commit["sha"])
    print("https://github.com/%s/%s/commit/%s" % (OWNER, REPO, commit["sha"]))


OWNER = REPO = ""

if __name__ == "__main__":
    main()