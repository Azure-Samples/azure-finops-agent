<template>
  <section class="turn-failure" role="alert">
    <AppIcon class="turn-failure-icon" name="error" size="20" />
    <div class="turn-failure-body">
      <h3>{{ failure.title }}</h3>
      <p>{{ failure.text }}</p>
      <p class="turn-failure-hint">{{ failure.hint }}</p>
      <template v-if="showEdit">
        <button type="button" :disabled="!canEdit" @click="$emit('edit')">Edit saved question</button>
        <p class="turn-failure-hint">Nothing is sent automatically. Your current draft will not be overwritten.</p>
      </template>
    </div>
  </section>
</template>

<script setup>
import AppIcon from "./AppIcon.vue";

defineProps({
  failure: { type: Object, required: true },
  canEdit: { type: Boolean, default: false },
  showEdit: { type: Boolean, default: true },
});
defineEmits(["edit"]);
</script>

<style scoped>
.turn-failure {
  display: flex;
  gap: 12px;
  width: 100%;
  max-width: 756px;
  padding: 16px;
  border: 1px solid var(--danger-border);
  border-left: 3px solid var(--danger);
  border-radius: var(--radius);
  background: var(--danger-soft);
  color: var(--ink);
  text-align: left;
}
.turn-failure-icon {
  flex-shrink: 0;
  color: var(--danger);
  margin-top: 2px;
}
.turn-failure-body {
  min-width: 0;
  overflow-wrap: anywhere;
}
h3 {
  margin: 0 0 6px;
  font-size: var(--text-title-size);
  line-height: var(--text-title-line);
  font-weight: 500;
}
p {
  margin: 0 0 10px;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
}
.turn-failure-hint {
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
  color: var(--text-muted);
}
p:last-child {
  margin-bottom: 0;
}
button {
  margin-bottom: 8px;
  padding: 6px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
  color: var(--ink);
  font: inherit;
  cursor: pointer;
}
button:hover:not(:disabled) {
  background: var(--tint);
}
button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
button:disabled {
  opacity: 0.5;
  cursor: default;
}
</style>
