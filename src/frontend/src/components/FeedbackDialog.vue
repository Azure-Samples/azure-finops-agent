<template>
  <Teleport to="body">
    <div
      v-if="open"
      class="feedback-overlay"
      @click.self="close"
    >
      <div
        ref="dialogEl"
        class="feedback-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="feedback-title"
        aria-describedby="feedback-subtitle"
        @keydown.tab="trapFocus"
      >
        <div class="feedback-header">
          <div class="feedback-heading">
            <h2 id="feedback-title" class="feedback-title">
              {{ context ? "Feedback on this answer" : "Send feedback" }}
            </h2>
            <p id="feedback-subtitle" class="feedback-subtitle">
              {{
                mode === "link"
                  ? "GitHub opens with your feedback filled in, so you can post it with your GitHub account."
                  : mode === "direct"
                    ? "Goes straight to the maintainers as a public GitHub issue. No GitHub account needed."
                    : "Your feedback becomes a public GitHub issue for the maintainers."
              }}
            </p>
          </div>
          <button
            type="button"
            class="feedback-close"
            aria-label="Close"
            @click="close"
          >
            <AppIcon name="close" size="20" />
          </button>
        </div>

        <div v-if="result" class="feedback-body feedback-done" role="status">
          <AppIcon name="checkCircle" size="32" class="feedback-done-icon" />
          <template v-if="result.mode === 'direct'">
            <p class="feedback-done-title">
              Thank you! Your feedback is issue #{{ result.number }}.
            </p>
            <p class="feedback-done-text">
              Follow it on GitHub to see the maintainers' reply.
            </p>
          </template>
          <template v-else>
            <p class="feedback-done-title">One more step on GitHub</p>
            <p class="feedback-done-text">
              GitHub opens with your feedback filled in. Sign in, check it,
              and select <strong>Create</strong>.
            </p>
          </template>
        </div>

        <form
          v-else
          id="feedback-form"
          class="feedback-body"
          novalidate
          @submit.prevent="submit"
        >
          <fieldset class="feedback-kinds">
            <legend class="feedback-label">What is it about?</legend>
            <div class="feedback-kind-row">
              <label
                v-for="option in KINDS"
                :key="option.value"
                class="feedback-kind"
                :class="{ 'feedback-kind--selected': kind === option.value }"
              >
                <input
                  v-model="kind"
                  class="feedback-kind-input"
                  type="radio"
                  name="feedback-kind"
                  :value="option.value"
                />
                <span>{{ option.label }}</span>
              </label>
            </div>
          </fieldset>

          <label class="feedback-label" for="feedback-summary">Summary</label>
          <input
            id="feedback-summary"
            ref="summaryEl"
            v-model="title"
            class="feedback-input"
            type="text"
            maxlength="120"
            autocomplete="off"
            :placeholder="PLACEHOLDERS[kind].title"
            :aria-invalid="errorField === 'title' ? 'true' : undefined"
          />

          <label class="feedback-label" for="feedback-details">
            Details <span class="feedback-optional">(optional)</span>
          </label>
          <textarea
            id="feedback-details"
            ref="detailsEl"
            v-model="details"
            class="feedback-input feedback-textarea"
            rows="5"
            maxlength="4000"
            :placeholder="PLACEHOLDERS[kind].details"
            :aria-invalid="errorField === 'details' ? 'true' : undefined"
          ></textarea>

          <template v-if="context?.question">
            <label class="feedback-include">
              <input v-model="includeQuestion" type="checkbox" />
              <span>Include the question I asked</span>
            </label>
            <blockquote
              v-if="includeQuestion"
              class="feedback-quote"
              :aria-invalid="errorField === 'question' ? 'true' : undefined"
            >{{ context.question }}</blockquote>
          </template>

          <p class="feedback-notice">
            <AppIcon name="shield" size="16" />
            <span
              >Feedback is posted publicly on GitHub. Leave out names, IDs,
              email addresses, costs and anything else private.</span
            >
          </p>
          <p v-if="error" class="feedback-error" role="alert">{{ error }}</p>
        </form>

        <div class="feedback-footer">
          <template v-if="result">
            <button type="button" class="feedback-secondary" @click="close">
              Close
            </button>
            <a
              ref="resultLinkEl"
              class="feedback-primary"
              :href="result.url"
              target="_blank"
              rel="noopener noreferrer"
              >{{
                result.mode === "direct" ? "View on GitHub" : "Open GitHub"
              }}</a
            >
          </template>
          <template v-else>
            <a
              v-if="fallbackUrl"
              class="feedback-fallback"
              :href="fallbackUrl"
              target="_blank"
              rel="noopener noreferrer"
              >Post it on GitHub instead</a
            >
            <button type="button" class="feedback-secondary" @click="close">
              Cancel
            </button>
            <!-- Not disabled while sending: a disabled button drops focus to
                 the page, and Escape and Tab would leave the form. -->
            <button
              type="submit"
              form="feedback-form"
              class="feedback-primary"
              :aria-disabled="sending ? 'true' : undefined"
            >
              {{
                sending
                  ? "Sending…"
                  : mode === "link"
                    ? "Continue on GitHub"
                    : "Send feedback"
              }}
            </button>
          </template>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<script setup>
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import AppIcon from "./AppIcon.vue";

// `context` is set when the form is about one answer: { turn, question, rating }.
const props = defineProps({
  open: { type: Boolean, default: false },
  context: { type: Object, default: null },
});
const emit = defineEmits(["close"]);

const KINDS = [
  { value: "bug", label: "Bug" },
  { value: "idea", label: "Idea" },
  { value: "answer", label: "Answer quality" },
  { value: "other", label: "Other" },
];
const PLACEHOLDERS = {
  bug: {
    title: "e.g. The chart didn't load",
    details: "What did you do, what happened, and what did you expect?",
  },
  idea: {
    title: "e.g. Compare two months side by side",
    details: "What would you like, and what would it help you do?",
  },
  answer: {
    title: "e.g. The total left out storage",
    details: "What was wrong, missing or unclear in the answer?",
  },
  other: {
    title: "e.g. Where do I see my savings?",
    details: "Tell us more.",
  },
};
const MIN_TITLE = 3;
const GITHUB = /^https:\/\/github\.com\//;

const dialogEl = ref(null);
const summaryEl = ref(null);
const detailsEl = ref(null);
const resultLinkEl = ref(null);
// "direct" or "link" once the server says; null until then, so the form never
// promises a path it may not take.
const mode = ref(null);
const kind = ref("idea");
const title = ref("");
const details = ref("");
const includeQuestion = ref(false);
const sending = ref(false);
const error = ref("");
const errorField = ref("");
const fallbackUrl = ref("");
const result = ref(null);
let draftKey = null;
let returnFocusTo = null;

// An error describes the text as it was sent; editing clears it.
watch([kind, title, details, includeQuestion], () => {
  error.value = "";
  errorField.value = "";
});

// Whether the server posts directly or hands back a GitHub link only changes
// the wording; the answer to the submission decides what actually happens.
// Asked when the page loads, so the form opens with the right wording.
let configRequest = null;
function loadMode() {
  configRequest ??= fetch("/api/feedback/config")
    .then((res) => (res.ok ? res.json() : {}))
    .catch(() => {
      configRequest = null;
      return {};
    });
  return configRequest.then((config) => {
    if (config.mode === "link" || config.mode === "direct")
      mode.value = config.mode;
  });
}
onMounted(loadMode);

watch(
  () => props.open,
  async (open) => {
    if (!open) {
      document.removeEventListener("keydown", onKeydown);
      const target = returnFocusTo;
      returnFocusTo = null;
      if (target?.isConnected) target.focus();
      return;
    }
    returnFocusTo = document.activeElement;
    // Escape closes the form wherever focus is, even if it left the dialog.
    document.addEventListener("keydown", onKeydown);
    // A draft survives closing the form, but belongs to what it was about.
    const key = props.context ? `answer-${props.context.turn}` : "general";
    if (key !== draftKey || result.value) {
      draftKey = key;
      kind.value = props.context ? "answer" : "idea";
      title.value = "";
      details.value = "";
      includeQuestion.value = false;
      result.value = null;
    }
    error.value = "";
    errorField.value = "";
    fallbackUrl.value = "";
    loadMode();
    await nextTick();
    summaryEl.value?.focus();
  },
);

function close() {
  if (!sending.value) emit("close");
}

function onKeydown(event) {
  if (event.key !== "Escape") return;
  event.stopPropagation();
  close();
}
onBeforeUnmount(() => document.removeEventListener("keydown", onKeydown));

function trapFocus(event) {
  const focusable = [
    ...(dialogEl.value?.querySelectorAll(
      'button:not([disabled]), a[href], input:not([disabled]), textarea:not([disabled])',
    ) || []),
  ];
  if (!focusable.length) return;
  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
}

async function submit() {
  if (sending.value) return;
  error.value = "";
  errorField.value = "";
  fallbackUrl.value = "";
  if (title.value.trim().length < MIN_TITLE) {
    error.value = "Add a short summary.";
    errorField.value = "title";
    summaryEl.value?.focus();
    return;
  }
  sending.value = true;
  try {
    const res = await fetch("/api/feedback", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        kind: kind.value,
        title: title.value,
        details: details.value,
        question:
          includeQuestion.value && props.context?.question
            ? props.context.question
            : undefined,
        rating: props.context?.rating || undefined,
      }),
    });
    const data = await res.json().catch(() => ({}));
    if (res.ok && GITHUB.test(data.url || "")) {
      result.value = {
        mode: data.mode === "direct" ? "direct" : "link",
        number: data.number,
        url: data.url,
      };
      window.__trackAppInsightsEvent?.("feedback.sent", {
        mode: result.value.mode,
        kind: kind.value,
      });
      await nextTick();
      resultLinkEl.value?.focus();
      return;
    }
    error.value =
      data.error || "Your feedback couldn't be sent. Try again in a moment.";
    errorField.value = data.field || "";
    if (GITHUB.test(data.fallbackUrl || "")) fallbackUrl.value = data.fallbackUrl;
    // Take the user to what needs changing.
    const field = { title: summaryEl, details: detailsEl }[errorField.value];
    field?.value?.focus();
  } catch {
    error.value =
      "Your feedback couldn't be sent. Check your connection and try again.";
  } finally {
    sending.value = false;
  }
}
</script>

<style scoped>
.feedback-overlay {
  position: fixed;
  inset: 0;
  z-index: 3000;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  background: var(--backdrop);
  backdrop-filter: blur(3px);
}
.feedback-dialog {
  display: flex;
  flex-direction: column;
  width: min(560px, 100%);
  max-height: min(92vh, 760px);
  overflow: hidden;
  background: var(--surface);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow);
}
.feedback-header {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  padding: 20px 24px 16px;
  border-bottom: 1px solid var(--border);
}
.feedback-heading {
  flex: 1;
  min-width: 0;
}
.feedback-title {
  margin: 0;
  font-size: var(--text-title-size);
  line-height: var(--text-title-line);
  font-weight: 500;
  color: var(--ink);
}
.feedback-subtitle {
  margin: 4px 0 0;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  color: var(--text-muted);
}
.feedback-close {
  flex: 0 0 auto;
  padding: 6px;
  border: none;
  border-radius: var(--radius);
  background: none;
  line-height: 1;
  color: var(--text-muted);
  cursor: pointer;
}
.feedback-close:hover {
  background: var(--hover);
  color: var(--ink);
}
.feedback-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 16px 24px 20px;
  overflow-y: auto;
}
.feedback-body > * {
  flex-shrink: 0;
}
.feedback-kinds {
  margin: 0;
  padding: 0;
  border: none;
}
.feedback-label {
  margin-top: 8px;
  padding: 0;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  font-weight: 500;
  color: var(--ink);
}
.feedback-kinds .feedback-label {
  margin: 0 0 8px;
}
.feedback-optional {
  font-weight: 400;
  color: var(--text-muted);
}
.feedback-kind-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.feedback-kind {
  position: relative;
  padding: 6px 14px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  color: var(--ink);
  cursor: pointer;
}
.feedback-kind:hover {
  background: var(--hover);
}
.feedback-kind--selected,
.feedback-kind--selected:hover {
  border-color: var(--accent);
  background: var(--accent-soft);
  color: var(--accent);
}
.feedback-kind:focus-within {
  box-shadow: 0 0 0 3px var(--accent-soft-strong);
}
.feedback-kind-input {
  position: absolute;
  opacity: 0;
  pointer-events: none;
}
.feedback-input {
  width: 100%;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
  font: inherit;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  color: var(--ink);
}
.feedback-input::placeholder {
  color: var(--text-muted);
}
.feedback-input:focus {
  outline: none;
  border-color: var(--accent);
  box-shadow: 0 0 0 3px var(--accent-soft-strong);
}
.feedback-input[aria-invalid="true"],
.feedback-quote[aria-invalid="true"] {
  border-color: var(--danger);
}
.feedback-textarea {
  min-height: 112px;
  resize: vertical;
}
.feedback-include {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 4px;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  color: var(--ink);
  cursor: pointer;
}
.feedback-quote {
  margin: 0;
  padding: 8px 12px;
  max-height: 96px;
  overflow-y: auto;
  border: 1px solid var(--border);
  border-left: 3px solid var(--accent);
  border-radius: var(--radius);
  background: var(--tint);
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
  color: var(--text-muted);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.feedback-notice {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  margin: 8px 0 0;
  padding: 8px 12px;
  border: 1px solid var(--warning-border);
  border-radius: var(--radius);
  background: var(--warning-soft);
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
  color: var(--ink);
}
.feedback-notice .app-icon {
  margin-top: 1px;
  color: var(--warning);
}
.feedback-error {
  margin: 0;
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
  color: var(--danger);
}
.feedback-done {
  align-items: center;
  padding: 28px 24px;
  text-align: center;
}
.feedback-done-icon {
  color: var(--success);
}
.feedback-done-title {
  margin: 0;
  font-size: var(--text-title-size);
  line-height: var(--text-title-line);
  font-weight: 500;
  color: var(--ink);
}
.feedback-done-text {
  margin: 0;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  color: var(--text-muted);
}
.feedback-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  flex-wrap: wrap;
  gap: 8px;
  padding: 16px 24px;
  border-top: 1px solid var(--border);
}
.feedback-fallback {
  margin-right: auto;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  color: var(--accent);
}
.feedback-primary,
.feedback-secondary {
  display: inline-flex;
  align-items: center;
  padding: 8px 16px;
  border-radius: var(--radius);
  font: inherit;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  text-decoration: none;
  cursor: pointer;
}
.feedback-primary {
  border: 1px solid var(--accent);
  background: var(--accent);
  color: #fff;
  transition: background var(--motion-fast);
}
.feedback-primary:hover:not([aria-disabled="true"]) {
  background: var(--accent-hover);
}
.feedback-primary[aria-disabled="true"] {
  opacity: 0.6;
  cursor: default;
}
.feedback-secondary {
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--ink);
}
.feedback-secondary:hover {
  background: var(--hover);
}
@media (max-width: 600px) {
  .feedback-overlay {
    padding: 12px;
  }
  .feedback-header,
  .feedback-body,
  .feedback-footer {
    padding-left: 16px;
    padding-right: 16px;
  }
}
</style>
