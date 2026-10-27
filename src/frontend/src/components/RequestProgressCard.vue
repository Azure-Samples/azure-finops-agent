<template>
  <section
    class="request-progress"
    :class="{
      'request-progress--paused': paused,
      'request-progress--stopped': progress.phase === 'stopped',
    }"
    :data-phase="progress.phase"
    role="group"
    aria-label="Request progress"
  >
    <div class="request-progress-art" aria-hidden="true">
      <AppIcon
        class="request-progress-cloud request-progress-main-icon"
        :name="progress.phase === 'stopped' ? 'stop' : 'schedule'"
        size="32"
      />
      <span v-if="progress.phase !== 'stopped'" class="request-progress-dots">
        <span class="request-progress-dot"></span>
        <span class="request-progress-dot"></span>
        <span class="request-progress-dot"></span>
      </span>
    </div>
    <div class="request-progress-body">
      <div class="request-progress-header">
        <h3><span role="status" aria-live="polite" aria-atomic="true">{{ progress.heading }}</span></h3>
        <span class="request-progress-badge">{{ progress.badge }}</span>
      </div>
      <p class="request-progress-explanation">{{ progress.explanation }}</p>
      <div
        v-if="progress.remaining !== null && (progress.phase !== 'stopped' || progress.remaining > 0)"
        class="request-progress-timing"
        aria-live="off"
      >
        <div class="request-progress-countdown">
          <span class="request-progress-countdown-value">{{ progress.countdown }}</span>
          <span>{{ progress.countdownLabel }}</span>
        </div>
        <div v-if="progress.retryAtUtc" class="request-progress-deadline">
          Service deadline
          <time :datetime="progress.retryAtUtc">{{ progress.retryTime }}</time>
        </div>
      </div>
      <div
        v-if="progress.phase === 'cooldown'"
        class="request-progress-track"
        role="progressbar"
        aria-label="Cooldown elapsed, not request completion"
        aria-valuemin="0"
        aria-valuemax="100"
        :aria-valuenow="progress.percent"
        :aria-valuetext="`${progress.remaining} seconds until the retry window`"
        aria-live="off"
      >
        <span class="request-progress-fill" :style="{ width: `${progress.percent}%` }"></span>
      </div>
      <p class="request-progress-guidance">{{ progress.guidance }}</p>
      <p v-if="progress.resourceNote" class="request-progress-resource-note">{{ progress.resourceNote }}</p>
    </div>
  </section>
</template>

<script setup>
import AppIcon from "./AppIcon.vue";

defineProps({
  progress: { type: Object, required: true },
  paused: { type: Boolean, default: false },
});
</script>

<style scoped>
.request-progress {
  display: grid;
  grid-template-columns: 44px minmax(0, 1fr);
  gap: 12px;
  width: 100%;
  max-width: 756px;
  min-width: 0;
  padding: 16px 18px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--tint);
  color: var(--ink);
  text-align: left;
}
.request-progress-art {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  color: var(--accent);
}
.request-progress-cloud {
  animation: request-breathe 3s ease-in-out infinite;
}
.request-progress-dots {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}
.request-progress-dot {
  width: 4px;
  height: 4px;
  border-radius: 50%;
  background: currentColor;
  animation: request-dot 2.4s ease-in-out infinite;
}
.request-progress-dot:nth-child(2) {
  animation-delay: .2s;
}
.request-progress-dot:nth-child(3) {
  animation-delay: .4s;
}
.request-progress-body {
  min-width: 0;
  overflow-wrap: anywhere;
}
.request-progress-header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 12px;
}
h3 {
  flex: 1 1 220px;
  margin: 0;
  font-size: var(--text-title-size);
  font-weight: 500;
  line-height: var(--text-title-line);
}
.request-progress-badge {
  padding: 2px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
  color: var(--text-muted);
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
  white-space: nowrap;
}
p {
  margin: 8px 0 0;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
}
.request-progress-timing {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px 20px;
  margin-top: 10px;
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
  color: var(--text-muted);
}
.request-progress-countdown,
.request-progress-deadline {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.request-progress-countdown-value {
  color: var(--ink);
  font-size: var(--text-heading-size);
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  line-height: var(--text-heading-line);
}
time {
  color: var(--ink);
  font-variant-numeric: tabular-nums;
}
.request-progress-track {
  height: 4px;
  margin-top: 10px;
  overflow: hidden;
  border-radius: 4px;
  background: var(--accent-soft-strong);
}
.request-progress-fill {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: var(--accent);
  transition: width .25s linear;
}
.request-progress-resource-note {
  color: var(--text-muted);
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
}
.request-progress--stopped {
  border-color: var(--warning-border);
  background: var(--warning-soft);
}
.request-progress--stopped .request-progress-art,
.request-progress--stopped .request-progress-badge,
.request-progress--stopped .request-progress-countdown-value {
  color: var(--warning);
}
.request-progress--stopped .request-progress-badge {
  border-color: var(--warning-border);
}
.request-progress--stopped * {
  animation: none;
  transition: none;
}
.request-progress--paused,
.request-progress--paused * {
  animation-play-state: paused;
  transition: none;
}
@keyframes request-breathe {
  0%, 100% { transform: translateY(0); }
  50% { transform: translateY(-3px); }
}
@keyframes request-dot {
  0%, 100% { opacity: .35; }
  50% { opacity: 1; }
}
@media (max-width: 480px) {
  .request-progress {
    grid-template-columns: 32px minmax(0, 1fr);
    gap: 10px;
    padding: 12px;
  }
  h3 {
    flex-basis: 100%;
  }
}
@media (prefers-reduced-motion: reduce) {
  .request-progress,
  .request-progress * {
    animation: none;
    transition: none;
  }
}
</style>
