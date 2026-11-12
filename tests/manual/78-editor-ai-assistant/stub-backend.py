#!/usr/bin/env python3
"""Stub assistant backend for manual test plan #78 (editor AI assistant, #1015).

Implements the AG-UI agent POST /assistant/agent of the Fleans.Assistant.Contracts contract (#1054) with canned answers.
Standard library only:  python3 stub-backend.py --mode valid [--port 5300]

Modes:
  valid    text + a valid layout-free proposal
  fix      first an invalid proposal, then (on the automatic "fix:" request) a valid one
  invalid  always an invalid proposal (the editor gives up after 2 automatic fixes)
  text     text only, no proposal
  slow     streams one word per second for 10 s, then a valid proposal (try Stop)
  error    a RUN_ERROR event
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


def sse(event):
    return f"data: {json.dumps(event)}\n\n".encode()


def text(delta):
    return sse({"type": "TEXT_MESSAGE_CONTENT", "messageId": "m1", "delta": delta})


def proposal(xml):
    # A call of the editor's frontend tool propose_bpmn_diagram.
    return (sse({"type": "TOOL_CALL_START", "toolCallId": "p1", "toolCallName": "propose_bpmn_diagram"})
            + sse({"type": "TOOL_CALL_ARGS", "toolCallId": "p1", "delta": json.dumps({"bpmnXml": xml})})
            + sse({"type": "TOOL_CALL_END", "toolCallId": "p1"}))


class Handler(BaseHTTPRequestHandler):
    mode = "valid"

    def do_POST(self):
        if self.path.rstrip("/") != "/assistant/agent":
            self.send_error(404)
            return
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        messages = body.get("messages", [])
        last = messages[-1].get("content", "") if messages else ""
        context = {c["description"]: c["value"] for c in body.get("context", [])}
        plugins = json.loads(context.get("fleans.plugins", "[]"))
        print(f"<- {len(messages)} messages, currentXml={'yes' if body.get('state', {}).get('bpmnXml') else 'null'}, "
              f"plugins={[p['taskType'] for p in plugins]}, engineVersion={context.get('fleans.engineVersion')}, "
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
        run = {"threadId": body.get("threadId"), "runId": body.get("runId")}
        write(sse({"type": "RUN_STARTED", **run}))
        write(sse({"type": "TEXT_MESSAGE_START", "messageId": "m1", "role": "assistant"}))

        if self.mode == "error":
            write(text("Thinking..."))
            write(sse({"type": "RUN_ERROR", "code": "stub_error", "message": "The stub refuses this request."}))
            return
        elif self.mode == "text":
            write(text("I only talk in this mode."))
            write(sse({"type": "TEXT_MESSAGE_END", "messageId": "m1"}))
        elif self.mode == "slow":
            for word in "this answer arrives one word per second so you can press stop".split():
                write(text(word + " "))
                time.sleep(1)
            write(sse({"type": "TEXT_MESSAGE_END", "messageId": "m1"}))
            write(proposal(VALID))
        else:
            invalid = self.mode == "invalid" or (self.mode == "fix" and not last.startswith("fix: "))
            write(text("Here is an approval process with a 4 h escalation."))
            write(sse({"type": "TEXT_MESSAGE_END", "messageId": "m1"}))
            write(proposal(INVALID if invalid else VALID))
        write(sse({"type": "RUN_FINISHED", **run, "outcome": {"type": "success"}}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", default="valid",
                        choices=["valid", "fix", "invalid", "text", "slow", "error", "401", "429", "500"])
    parser.add_argument("--port", type=int, default=5300)
    args = parser.parse_args()
    Handler.mode = args.mode
    print(f"Stub assistant backend on http://localhost:{args.port}/ (mode {args.mode})", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
