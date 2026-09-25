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
      <svg width="44" height="44" viewBox="0 0 64 64" fill="none">
        <g class="request-progress-cloud">
          <path
            d="M18 40a10 10 0 0 1-1-20 15 15 0 0 1 29-1 11 11 0 0 1 0 22H18Z"
            fill="currentColor"
            fill-opacity=".12"
            stroke="currentColor"
            stroke-width="2"
            stroke-linejoin="round"
          />
          <path v-if="progress.phase === 'stopped'" d="M28 26v9m8-9v9" stroke="currentColor" stroke-width="3" stroke-linecap="round" />
          <path v-else d="M26 30h12" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
        </g>
        <g v-if="progress.phase !== 'stopped'" fill="currentColor">
          <circle class="request-progress-dot" cx="24" cy="51" r="2" />
          <circle class="request-progress-dot" cx="32" cy="51" r="2" />
          <circle class="request-progress-dot" cx="40" cy="51" r="2" />
        </g>
      </svg>
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
  max-width: 760px;
  min-width: 0;
  padding: 16px;
  border: 1px solid #c7e0f4;
  border-left: 3px solid #0078d4;
  border-radius: 8px;
  background: #f7fbff;
  color: #323130;
  box-shadow: 0 2px 6px #003b6408;
  text-align: left;
}
.request-progress-art {
  color: #0078d4;
}
.request-progress-art svg {
  display: block;
  max-width: 100%;
}
.request-progress-cloud {
  animation: request-breathe 3s ease-in-out infinite;
}
.request-progress-dot {
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
  font-size: 15px;
  font-weight: 600;
  line-height: 1.4;
}
.request-progress-badge {
  padding: 2px 7px;
  border: 1px solid #c7e0f4;
  border-radius: 4px;
  color: #005a9e;
  font-size: 11px;
  white-space: nowrap;
}
p {
  margin: 8px 0 0;
  font-size: 13px;
  line-height: 1.5;
}
.request-progress-timing {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px 20px;
  margin-top: 10px;
  font-size: 12px;
  color: #605e5c;
}
.request-progress-countdown,
.request-progress-deadline {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.request-progress-countdown-value {
  color: #005a9e;
  font-size: 24px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  line-height: 1.2;
}
time {
  color: #323130;
  font-variant-numeric: tabular-nums;
}
.request-progress-track {
  height: 4px;
  margin-top: 10px;
  overflow: hidden;
  border-radius: 4px;
  background: #deecf9;
}
.request-progress-fill {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: #0078d4;
  transition: width .25s linear;
}
.request-progress-resource-note {
  color: #605e5c;
  font-size: 12px;
}
.request-progress--stopped {
  border-color: #e6d5b8;
  border-left-color: #8a5e15;
  background: #fffaf2;
}
.request-progress--stopped .request-progress-art,
.request-progress--stopped .request-progress-badge,
.request-progress--stopped .request-progress-countdown-value {
  color: #805600;
}
.request-progress--stopped .request-progress-badge {
  border-color: #e6d5b8;
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
