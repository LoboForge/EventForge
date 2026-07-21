#!/usr/bin/env python3
"""Ad-hoc helper: print Vast instance status for target wan boxes."""
import json
import os
import sys
import urllib.request

VAST_KEY = os.environ.get("VAST_API_KEY", "")


def get_instance(iid):
    req = urllib.request.Request(
        f"https://console.vast.ai/api/v0/instances/{iid}/",
        headers={"Authorization": f"Bearer {VAST_KEY}"},
    )
    with urllib.request.urlopen(req, timeout=30) as r:
        d = json.load(r)
    return d.get("instances") or d


def main():
    ids = sys.argv[1:] or ["45255378", "45256646"]
    for iid in ids:
        try:
            i = get_instance(iid)
            print(
                f"{iid}: actual={i.get('actual_status')} cur={i.get('cur_state')} "
                f"intended={i.get('intended_status')} "
                f"ssh={i.get('ssh_host')}:{i.get('ssh_port')} "
                f"gpu={i.get('gpu_name')} disk={i.get('disk_space')} "
                f"msg={str(i.get('status_msg'))[:110]}"
            )
        except Exception as e:
            print(f"{iid}: ERROR {e}")


if __name__ == "__main__":
    main()
