#!/usr/bin/env python3
"""Stub assistant backend for manual test plan #78 (editor AI assistant, #1015).

Implements POST /assistant/chat of the Fleans.Assistant.Contracts contract with canned answers.
Standard library only:  python3 stub-backend.py --mode valid [--port 5300]

Modes:
  valid    text + a valid layout-free proposal
  fix      first an invalid proposal, then (on the automatic "fix:" request) a valid one
  invalid  always an invalid proposal (the editor gives up after 2 automatic fixes)
  text     text only, no proposal
  slow     streams one word per second for 10 s, then a valid proposal (try Stop)
  error    an `error` event
  401 / 429 / 500   that HTTP status with an AssistantError body
"""
import argparse, json, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

VALID = """<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs" targetNamespace="http://bpmn.io/schema/bpmn">
  <process id="manual_ai_approval" isExecutable="true">
    <startEvent id="start" />
    <userTask id="approve" name="Approve request" />
    <boundaryEvent id="escalate" attachedToRef="approve"><timerEventDefinition><timeDuration>PT4H</timeDuration></timerEventDefinition></boundaryEvent>
    <endEvent id="approved" name="Approved" />
    <endEvent id="escalated" name="Escalated" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
    <sequenceFlow id="f2" sourceRef="approve" targetRef="approved" />
    <sequenceFlow id="f3" sourceRef="escalate" targetRef="escalated" />
  </process>
</definitions>"""
INVALID = '<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"><process id="broken">'


def sse(name, data):
    return f"event: {name}\ndata: {json.dumps(data)}\n\n".encode()


class Handler(BaseHTTPRequestHandler):
    mode = "valid"

    def do_POST(self):
        if self.path.rstrip("/") != "/assistant/chat":
            self.send_error(404)
            return
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        messages = body.get("messages", [])
        last = messages[-1]["text"] if messages else ""
        print(f"<- {len(messages)} messages, currentXml={'yes' if body.get('currentXml') else 'null'}, "
              f"plugins={[p['taskType'] for p in body.get('plugins', [])]}, engineVersion={body.get('engineVersion')}, "
              f"auth={'Bearer' if self.headers.get('Authorization', '').startswith('Bearer ') else 'none'}, "
              f"last={'fix: ...' if last.startswith('fix: ') else 'user message'}", flush=True)

        if self.mode in ("401", "429", "500"):
            payload = json.dumps({"code": "stub", "message": f"Stub answered {self.mode}"}).encode()
            self.send_response(int(self.mode))
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        write = lambda chunk: (self.wfile.write(chunk), self.wfile.flush())

        if self.mode == "error":
            write(sse("text", {"text": "Thinking..."}))
            write(sse("error", {"code": "stub_error", "message": "The stub refuses this request."}))
        elif self.mode == "text":
            write(sse("text", {"text": "I only talk in this mode."}))
        elif self.mode == "slow":
            for word in "this answer arrives one word per second so you can press stop".split():
                write(sse("text", {"text": word + " "}))
                time.sleep(1)
            write(sse("proposal", {"bpmnXml": VALID}))
        else:
            invalid = self.mode == "invalid" or (self.mode == "fix" and not last.startswith("fix: "))
            write(sse("text", {"text": "Here is an approval process with a 4 h escalation."}))
            write(sse("proposal", {"bpmnXml": INVALID if invalid else VALID}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", default="valid",
                        choices=["valid", "fix", "invalid", "text", "slow", "error", "401", "429", "500"])
    parser.add_argument("--port", type=int, default=5300)
    args = parser.parse_args()
    Handler.mode = args.mode
    print(f"Stub assistant backend on http://localhost:{args.port}/ (mode {args.mode})", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
