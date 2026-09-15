import gzip
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request


def run():
    assert os.getuid() != 0, "The application image must run as non-root"
    for directory in ("/home/copilot", "/home/dataprotection-keys"):
        with tempfile.TemporaryFile(dir=directory) as probe:
            probe.write(b"storage-check")

    runtimes = list(Path("/app").rglob("copilot-runtime"))
    assert len(runtimes) == 1, "Expected exactly one bundled CLI runtime"
    assert (runtimes[0].parent / "runtime.node").is_file()
    assert os.access(runtimes[0], os.X_OK), "Bundled runtime must be executable"
    subprocess.run(["python3", "-c", "import pandas, openpyxl, pyarrow, pdfminer"], check=True, timeout=30)
    check_application("")
    check_application("InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://127.0.0.1:9/;LiveEndpoint=https://127.0.0.1:9/")
    received = []
    delivered = threading.Event()

    class Ingestion(BaseHTTPRequestHandler):
        def do_POST(self):
            payload = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            if self.headers.get("Content-Encoding") == "gzip":
                payload = gzip.decompress(payload)
            try:
                parsed = json.loads(payload)
                items = parsed if isinstance(parsed, list) else [parsed]
            except json.JSONDecodeError:
                items = [json.loads(line) for line in payload.splitlines() if line]
            received.extend(items)
            host_requests = [item for item in received if item.get("data", {}).get("baseType") == "RequestData"
                             and "/api/version" in item.get("data", {}).get("baseData", {}).get("name", "")]
            collector_spans = [item for item in received if item.get("data", {}).get("baseData", {}).get("name") == "synthetic-collector-smoke"]
            if len(host_requests) >= 21 and collector_spans:
                delivered.set()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"itemsReceived": len(items), "itemsAccepted": len(items), "errors": []}).encode())

        def log_message(self, format, *arguments):
            pass

    with ThreadingHTTPServer(("127.0.0.1", 0), Ingestion) as server:
        receiver = threading.Thread(target=server.serve_forever, daemon=True)
        receiver.start()
        try:
            check_application(f"InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:{server.server_port}/;LiveEndpoint=https://127.0.0.1:9/", delivered)
        finally:
            server.shutdown()
            receiver.join(timeout=5)


def check_application(connection_string, delivered=None):
    with tempfile.TemporaryDirectory(prefix="finops-container-") as directory:
        artifact_id = "a" * 32
        artifacts = Path(directory) / "artifacts"
        artifacts.mkdir()
        (artifacts / f"{artifact_id}.data").write_text("synthetic persisted report", encoding="utf-8")
        (artifacts / f"{artifact_id}.json").write_text(json.dumps({
            "Id": artifact_id, "Owner": 101, "FileName": "synthetic.html", "ContentType": "text/html",
            "ExpiresUtc": (datetime.now(timezone.utc) + timedelta(hours=1)).isoformat(),
        }), encoding="utf-8")
        environment = {
            **os.environ,
            "AzureOpenAI__Endpoint": "https://example.invalid/",
            "AzureOpenAI__DeploymentName": "synthetic-test-model",
            "COPILOT_HOME": directory,
            "ASPNETCORE_URLS": "http://127.0.0.1:8080",
            "ASPNETCORE_ENVIRONMENT": "Production",
            "APPLICATIONINSIGHTS_CONNECTION_STRING": connection_string,
            "ApplicationInsights__ConnectionString": connection_string,
            "OTEL_EXPORTER_OTLP_ENDPOINT": "",
        }
        if connection_string:
            subprocess.run(["/usr/local/bin/otelcol", "validate", "--config", "/etc/otelcol/config.yaml"], env=environment, check=True, timeout=30)
        process = subprocess.Popen(
            ["/usr/local/bin/entrypoint.sh"], cwd="/app", env=environment,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, start_new_session=True,
        )
        lines = []
        ready = threading.Event()

        def collect_output():
            for line in process.stdout:
                lines.append(line.rstrip())
                if "Now listening on:" in line:
                    ready.set()

        reader = threading.Thread(target=collect_output, daemon=True)
        reader.start()
        try:
            assert ready.wait(60), "Application did not start:\n" + "\n".join(lines[-30:])
            request = urllib.request.Request("http://127.0.0.1:8080/api/version", headers={"X-Forwarded-Proto": "https"})
            with urllib.request.urlopen(request, timeout=10) as response:
                version = json.load(response)
                assert response.status == 200
                assert all(key in version for key in ("sha", "build", "branch"))
            request = urllib.request.Request(f"http://127.0.0.1:8080/api/download/file/{artifact_id}", headers={"X-Forwarded-Proto": "https"})
            try:
                urllib.request.urlopen(request, timeout=10).close()
                raise AssertionError("A persisted artifact was served to a different owner")
            except urllib.error.HTTPError as error:
                assert error.code == 404, f"Persisted artifact startup failed with HTTP {error.code}"
            if connection_string:
                request = urllib.request.Request("http://127.0.0.1:4318/v1/traces", data=b'{"resourceSpans":[]}', headers={"Content-Type": "application/json"})
                with urllib.request.urlopen(request, timeout=10) as response:
                    assert response.status == 200, "Collector must accept OTLP on loopback"
            if delivered is not None:
                started = time.monotonic()
                for index in range(20):
                    request = urllib.request.Request("http://127.0.0.1:8080/api/version", headers={"X-Forwarded-Proto": "https"})
                    with urllib.request.urlopen(request, timeout=10) as response:
                        assert response.status == 200
                assert time.monotonic() - started < 2, "Sampling probe must exceed five requests per second"
                timestamp = time.time_ns()
                span = {"traceId": "1" * 32, "spanId": "2" * 16, "name": "synthetic-collector-smoke", "kind": 1,
                        "startTimeUnixNano": str(timestamp), "endTimeUnixNano": str(timestamp + 1000000)}
                payload = json.dumps({"resourceSpans": [{"scopeSpans": [{"spans": [span]}]}]}).encode()
                request = urllib.request.Request("http://127.0.0.1:4318/v1/traces", data=payload, headers={"Content-Type": "application/json"})
                with urllib.request.urlopen(request, timeout=10) as response:
                    assert response.status == 200
                assert delivered.wait(45), "Host burst or collector span did not reach local ingestion"
            children_path = Path(f"/proc/{process.pid}/task/{process.pid}/children")
            children = children_path.read_text().split()
            assert len(children) == (2 if connection_string else 1), "Entrypoint must supervise the application and optional collector"
            process.send_signal(signal.SIGTERM)
            process.wait(timeout=30)
            reader.join(timeout=5)
            assert process.returncode in (0, 143), f"Unexpected shutdown exit {process.returncode}"
            assert not any(Path(f"/proc/{child}").exists() for child in children), "Application process survived shutdown"
            assert "Application is shutting down..." in "\n".join(lines), ".NET did not receive termination"
            print(json.dumps({"nonRoot": True, "storageWritable": True, "cli": True, "http": True, "gracefulShutdown": True,
                              "offlineTelemetry": bool(connection_string) and delivered is None, "hostAndCollectorDelivery": delivered is not None}))
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
            reader.join(timeout=5)
            process.stdout.close()


if __name__ == "__main__":
    run()