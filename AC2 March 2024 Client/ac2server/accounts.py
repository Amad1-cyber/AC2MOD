"""Tiny local account store for the AC2 test server (accounts.json, salted PBKDF2 hashes)."""
import hashlib
import hmac
import json
import os

PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "accounts.json")


def _hash(password: str, salt: bytes) -> str:
    return hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 100_000).hex()


def _load() -> dict:
    try:
        with open(PATH) as fh:
            return json.load(fh)
    except FileNotFoundError:
        return {}


def create(username: str, password: str) -> None:
    accts = _load()
    salt = os.urandom(16)
    accts[username.lower()] = {"salt": salt.hex(), "hash": _hash(password, salt)}
    with open(PATH, "w") as fh:
        json.dump(accts, fh, indent=2)


def verify(username: str, password: str) -> bool:
    rec = _load().get(username.lower())
    if not rec:
        return False
    return hmac.compare_digest(rec["hash"], _hash(password, bytes.fromhex(rec["salt"])))


if __name__ == "__main__":
    import sys
    create(sys.argv[1], sys.argv[2])
    print("created", sys.argv[1])
