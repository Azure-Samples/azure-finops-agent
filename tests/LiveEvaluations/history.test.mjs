import test from "node:test";
import assert from "node:assert/strict";
import {
    classify,
    userQuestion,
    verifyOwner,
    validateGroups,
} from "./analyze-history.mjs";

test("host connection context does not become the user intent", () => {
    const question = userQuestion(
        "[CONTEXT: User IS connected to Azure. Available APIs: Azure ARM (QueryAzure). Proceed with tool calls directly.]\nWhere do I have H100 capacity?",
    );
    assert.equal(question, "Where do I have H100 capacity?");
    assert.equal(classify(question), "gpu-feasibility");
});

test("separates short acknowledgements and explicit probes", () => {
    assert.equal(classify("yes"), "excluded-short");
    assert.equal(
        classify("Synthetic incident verification. Calculate cost."),
        "excluded-probe",
    );
    assert.equal(classify("Assess my Crawl maturity"), "crawl");
});

test("anonymous metadata must match the exact outcome owner", () => {
    const start = { data: { context: { cwd: "/home/copilot/anon/123" } } };
    assert.equal(verifyOwner(start, "123", "/home/copilot"), true);
    assert.equal(verifyOwner(start, "124", "/home/copilot"), false);
    assert.equal(
        verifyOwner(
            { data: { context: { cwd: "/tmp/123" } } },
            "123",
            "/home/copilot",
        ),
        false,
    );
});

test("model group accounting cannot omit or double-count questions", () => {
    assert.doesNotThrow(() =>
        validateGroups([{ question: "Cost?", indices: [0, 1] }], 2),
    );
    assert.throws(() =>
        validateGroups([{ question: "Cost?", indices: [0, 0] }], 2),
    );
    assert.throws(() =>
        validateGroups([{ question: "Cost?", indices: [0] }], 2),
    );
});
