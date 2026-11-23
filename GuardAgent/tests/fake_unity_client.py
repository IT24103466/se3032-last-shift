"""Plays the part of Unity: connects to the brain server, sends ticks for a scripted scenario and prints the answers.
   python -m guard_agent.server --port 5599 &   python tests/fake_unity_client.py 5599"""
import json
import socket
import sys

port = int(sys.argv[1]) if len(sys.argv) > 1 else 5555
s = socket.create_connection(("127.0.0.1", port))
f = s.makefile("rw", encoding="utf-8", newline="\n")


def call(msg):
    f.write(json.dumps(msg) + "\n")
    f.flush()
    return json.loads(f.readline())


print(call({"type": "hello", "version": 1}))
print(call({"type": "episode_start"}))
base = dict(type="tick", dt=0.5, guard=dict(pos=[30, 46], hp=100, ammo=12, reloading=False), player_visible=False, player_pos=[0, 0], player_hp_frac=1.0,
            clear_shot=False, alarm=False, alarm_event_pos=[], noises=[], damage_taken=0, score_info=dict(damage_dealt_now=0, intruder_in_factory=False))
for i in range(4):
    print("patrol  ", call(dict(base)))
t = dict(base, alarm=True, alarm_event_pos=[47.5, 41.0], noises=[dict(pos=[47.5, 41.0], strength=0.7)])
print("alarm   ", call(t))
t = dict(base, alarm=True, player_visible=True, player_pos=[40, 44], clear_shot=True, score_info=dict(damage_dealt_now=8, intruder_in_factory=False))
print("sighted ", call(t))
t = dict(t, guard=dict(pos=[30, 46], hp=20, ammo=5, reloading=False), damage_taken=40, score_info=dict(damage_dealt_now=0, intruder_in_factory=True))
print("hurt    ", call(t))
t = dict(base, alarm=True, guard=dict(pos=[30, 46], hp=60, ammo=0, reloading=False))
print("no ammo ", call(t))
print(call({"type": "episode_end", "outcome": "kill"}))
