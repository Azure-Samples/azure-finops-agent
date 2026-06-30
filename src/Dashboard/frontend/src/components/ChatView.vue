<template>
  <div class="chat-view">
    <!-- Azure Portal-style top bar -->
    <header class="portal-header">
      <div class="portal-header-left">
        <button
          class="portal-burger"
          @click="sidebarOpen = !sidebarOpen"
          title="Toggle menu"
        >
          <svg
            width="18"
            height="18"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <line x1="3" y1="6" x2="21" y2="6" />
            <line x1="3" y1="12" x2="21" y2="12" />
            <line x1="3" y1="18" x2="21" y2="18" />
          </svg>
        </button>
        <a
          class="portal-trustline-link"
          href="https://github.com/Azure-Samples/azure-finops-agent"
          target="_blank"
          rel="noopener"
          title="View source on GitHub"
        >
          <svg
            width="12"
            height="12"
            viewBox="0 0 24 24"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.57.1.78-.25.78-.55 0-.27-.01-.99-.02-1.94-3.2.69-3.87-1.54-3.87-1.54-.52-1.32-1.27-1.67-1.27-1.67-1.04-.71.08-.7.08-.7 1.15.08 1.76 1.18 1.76 1.18 1.02 1.75 2.69 1.25 3.34.96.1-.74.4-1.25.72-1.54-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.28 1.18-3.09-.12-.29-.51-1.46.11-3.04 0 0 .97-.31 3.18 1.18a11.05 11.05 0 0 1 5.79 0c2.21-1.49 3.18-1.18 3.18-1.18.62 1.58.23 2.75.11 3.04.74.81 1.18 1.83 1.18 3.09 0 4.42-2.69 5.39-5.25 5.68.41.36.78 1.06.78 2.14 0 1.55-.01 2.8-.01 3.18 0 .31.21.66.79.55C20.21 21.39 23.5 17.08 23.5 12 23.5 5.65 18.35.5 12 .5z"
            />
          </svg>
          <span>Open source</span>
        </a>
      </div>
      <!-- Build/branch badge in the top-right corner. Highlights non-main
           (preview slot) deployments so it's obvious which build you're on. -->
      <div
        v-if="buildBranch && buildBranch !== 'main'"
        class="portal-build-badge portal-build-badge--preview"
        :title="`Branch ${buildBranch} · Build ${buildNumber} · ${buildSha}`"
      >
        <span class="portal-build-badge-branch">{{ buildBranch }}</span>
        <span class="portal-build-badge-sep">·</span>
        <span class="portal-build-badge-build">Build {{ buildNumber }}</span>
      </div>
      <div
        v-else-if="buildNumber && buildNumber !== '0'"
        class="portal-build-badge"
        :title="`Branch ${buildBranch || 'main'} · Build ${buildNumber} · ${buildSha}`"
      >
        <span class="portal-build-badge-branch">{{
          buildBranch || "main"
        }}</span>
        <span class="portal-build-badge-sep">·</span>
        <span class="portal-build-badge-build">Build {{ buildNumber }}</span>
      </div>
      <!-- Hidden — email + disconnect are already shown in the sidebar.
           Re-enable by removing v-if="false". -->
      <div
        v-if="false && azureConnected && azureUserEmail"
        class="portal-header-right"
      >
        <span class="portal-header-email">{{ azureUserEmail }}</span>
        <button
          class="portal-header-disconnect"
          @click="disconnectAzure"
          title="Disconnect Azure"
        >
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <line x1="18" y1="6" x2="6" y2="18" />
            <line x1="6" y1="6" x2="18" y2="18" />
          </svg>
        </button>
      </div>
    </header>

    <!-- Auth loading overlay -->
    <div v-if="authLoading" class="auth-overlay">
      <div class="auth-overlay-card">
        <div class="auth-overlay-spinner"></div>
        <p class="auth-overlay-text">Connecting to Azure...</p>
        <p class="auth-overlay-sub">
          {{
            azureConnected
              ? "Adding permissions..."
              : "Authenticating with Microsoft Entra ID..."
          }}
        </p>
      </div>
    </div>

    <!-- Main content area -->
    <div class="portal-body">
      <!-- Left sidebar -->
      <aside class="sidebar" :class="{ 'sidebar--collapsed': !sidebarOpen }">
        <div class="sidebar-scroll">
          <!-- Maturity score cards (Crawl / Walk / Run) — whole card is clickable -->
          <template v-if="azureConnected">
            <div
              v-for="cat in scoreCategories"
              :key="'score-' + cat.key"
              class="maturity-card"
              :class="{
                'maturity-card--scored': maturityScores[cat.key],
                'maturity-card--disabled': streaming,
              }"
              role="button"
              tabindex="0"
              :title="cat.scorePrompt"
              @click="!streaming && sendQuestion(cat.scorePrompt)"
              @keydown.enter="!streaming && sendQuestion(cat.scorePrompt)"
            >
              <div class="maturity-card-header">
                <div class="maturity-card-title">
                  <span class="maturity-card-label">{{ cat.label }}</span>
                  <span v-if="cat.subtitle" class="maturity-card-subtitle">{{
                    cat.subtitle
                  }}</span>
                </div>
                <span v-if="maturityScores[cat.key]" class="maturity-card-cta">
                  Re-score
                </span>
              </div>
              <div class="maturity-card-body">
                <span
                  class="maturity-card-stars"
                  :style="{
                    color: maturityScores[cat.key]
                      ? starColor(maturityOverall(cat.key))
                      : '#c8c6c4',
                  }"
                  >{{
                    maturityScores[cat.key]
                      ? starsText(maturityOverall(cat.key))
                      : "☆☆☆☆☆"
                  }}</span
                >
                <svg
                  v-if="maturityScores[cat.key]"
                  class="collapse-chevron maturity-card-chevron"
                  :class="{
                    'collapse-chevron--collapsed':
                      collapsedSections['cm_' + cat.key],
                  }"
                  viewBox="0 0 16 16"
                  fill="none"
                  role="button"
                  tabindex="0"
                  :aria-label="
                    collapsedSections['cm_' + cat.key] ? 'Expand' : 'Collapse'
                  "
                  @click.stop="toggleSection('cm_' + cat.key)"
                  @keydown.enter.stop="toggleSection('cm_' + cat.key)"
                >
                  <path
                    d="M4 6l4 4 4-4"
                    stroke="currentColor"
                    stroke-width="1.5"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  />
                </svg>
              </div>
              <!-- Per-dimension breakdown (only after scoring) -->
              <div
                v-if="maturityScores[cat.key]"
                class="collapse-body"
                :class="{
                  'collapse-body--collapsed': collapsedSections['cm_' + cat.key],
                }"
              >
                <div class="assessment-summary">
                  <div
                    v-for="sc in maturityScores[cat.key]"
                    :key="sc.id"
                    class="assessment-row"
                  >
                    <div class="assessment-label">{{ sc.label }}</div>
                    <div
                      class="assessment-stars"
                      :style="{ color: starColor(sc.score) }"
                    >
                      {{ starsText(sc.score) }}
                    </div>
                    <div class="assessment-detail-text">{{ sc.detail }}</div>
                  </div>
                </div>
              </div>
            </div>

            <!-- Playbook parent — collapses all detailed prompts under one node -->
            <div class="sidebar-category sidebar-category--border">
              <div
                class="sidebar-category-label sidebar-category-label--toggle"
                @click="toggleSection('playbookRoot')"
              >
                <div class="sidebar-category-left">
                  <span>All prompts</span>
                  <span class="sidebar-category-subtitle"
                    >Browse by maturity level</span
                  >
                </div>
                <div class="sidebar-category-right">
                  <svg
                    class="collapse-chevron"
                    :class="{
                      'collapse-chevron--collapsed':
                        collapsedSections.playbookRoot,
                    }"
                    viewBox="0 0 16 16"
                    fill="none"
                  >
                    <path
                      d="M4 6l4 4 4-4"
                      stroke="currentColor"
                      stroke-width="1.5"
                      stroke-linecap="round"
                      stroke-linejoin="round"
                    />
                  </svg>
                </div>
              </div>
              <div
                class="collapse-body"
                :class="{
                  'collapse-body--collapsed': collapsedSections.playbookRoot,
                }"
              >
                <div
                  v-for="grp in playbookGroups"
                  :key="grp.key"
                  class="sidebar-subgroup"
                >
                  <div
                    class="sidebar-subgroup-label sidebar-category-label--toggle"
                    @click="toggleSection('pb_' + grp.key)"
                  >
                    <span>{{ grp.label }}</span>
                    <svg
                      class="collapse-chevron"
                      :class="{
                        'collapse-chevron--collapsed':
                          collapsedSections['pb_' + grp.key],
                      }"
                      viewBox="0 0 16 16"
                      fill="none"
                    >
                      <path
                        d="M4 6l4 4 4-4"
                        stroke="currentColor"
                        stroke-width="1.5"
                        stroke-linecap="round"
                        stroke-linejoin="round"
                      />
                    </svg>
                  </div>
                  <div
                    class="collapse-body"
                    :class="{
                      'collapse-body--collapsed':
                        collapsedSections['pb_' + grp.key],
                    }"
                  >
                    <button
                      v-for="q in grp.prompts"
                      :key="q.label"
                      class="sidebar-question"
                      :disabled="streaming"
                      :title="q.prompt"
                      @click="sendQuestion(q.prompt)"
                    >
                      <span>{{ q.label }}</span>
                    </button>
                  </div>
                </div>
              </div>
            </div>
          </template>

          <!-- Pricing & Estimates — always visible, no login required -->
          <div
            class="sidebar-category"
            :class="{ 'sidebar-category--border': azureConnected }"
          >
            <div
              class="sidebar-category-label sidebar-category-label--toggle"
              @click="toggleSection('pricing')"
            >
              <div class="sidebar-category-left">
                <span>{{ pricingCategory.label }}</span>
              </div>
              <div class="sidebar-category-right">
                <svg
                  class="collapse-chevron"
                  :class="{
                    'collapse-chevron--collapsed': collapsedSections.pricing,
                  }"
                  viewBox="0 0 16 16"
                  fill="none"
                >
                  <path
                    d="M4 6l4 4 4-4"
                    stroke="currentColor"
                    stroke-width="1.5"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  />
                </svg>
              </div>
            </div>
            <div
              class="collapse-body"
              :class="{
                'collapse-body--collapsed': collapsedSections.pricing,
              }"
            >
              <button
                v-for="q in pricingPromptsForUser"
                :key="q.label"
                class="sidebar-question"
                :disabled="streaming"
                :title="q.prompt"
                @click="sendQuestion(q.prompt)"
              >
                <span>{{ q.label }}</span>
              </button>
            </div>
          </div>

          <!-- Subscriptions (after Azure login) -->
          <div
            v-if="azureConnected && azureSubscriptions.length"
            class="sidebar-category sidebar-category--border"
          >
            <div
              class="sidebar-category-label sidebar-category-label--toggle"
              @click="toggleSection('subs')"
            >
              <span>Subscriptions ({{ azureSubscriptions.length }})</span>
              <svg
                class="collapse-chevron"
                :class="{
                  'collapse-chevron--collapsed': collapsedSections.subs,
                }"
                viewBox="0 0 16 16"
                fill="none"
              >
                <path
                  d="M4 6l4 4 4-4"
                  stroke="currentColor"
                  stroke-width="1.5"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                />
              </svg>
            </div>
            <div
              class="collapse-body"
              :class="{ 'collapse-body--collapsed': collapsedSections.subs }"
            >
              <div
                v-for="sub in azureSubscriptions"
                :key="sub.id"
                class="sidebar-sub"
                :title="
                  sub.name +
                  '\n' +
                  sub.id +
                  (sub.tenantId ? '\nTenant: ' + sub.tenantId : '')
                "
              >
                <span class="sidebar-sub-name">{{ sub.name }}</span>
                <span class="sidebar-sub-id" :title="sub.id">{{ sub.id }}</span>
                <span
                  v-if="tenantNameFor(sub.tenantId)"
                  class="sidebar-sub-tenant"
                  :title="sub.tenantId"
                  >Tenant: {{ tenantNameFor(sub.tenantId) }}</span
                >
              </div>
            </div>
          </div>

          <!-- Knowledge Base (Entra-only — persistent org context) -->
          <div
            v-if="azureConnected"
            class="sidebar-category sidebar-category--border"
          >
            <div
              class="sidebar-category-label sidebar-category-label--toggle"
              @click="toggleSection('knowledge')"
            >
              <span>Knowledge Base ({{ knowledgeArticles.length }})</span>
              <svg
                class="collapse-chevron"
                :class="{
                  'collapse-chevron--collapsed': collapsedSections.knowledge,
                }"
                viewBox="0 0 16 16"
                fill="none"
              >
                <path
                  d="M4 6l4 4 4-4"
                  stroke="currentColor"
                  stroke-width="1.5"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                />
              </svg>
            </div>
            <div
              class="collapse-body"
              :class="{
                'collapse-body--collapsed': collapsedSections.knowledge,
              }"
            >
              <p class="kb-intro">
                Org context the agent applies to every conversation —
                subscription mappings, cost-center owners, SLAs, analysis rules.
                <a
                  href="https://github.com/Azure-Samples/azure-finops-agent/blob/main/docs/knowledge-authoring-guide.md"
                  target="_blank"
                  rel="noopener"
                  class="kb-help-link"
                  >How to write knowledge →</a
                >
              </p>

              <div
                v-for="a in knowledgeArticles"
                :key="a.id"
                class="kb-item"
                :class="{ 'kb-item--inactive': !a.active }"
                :title="a.title"
                @click="editKnowledgeArticle(a)"
              >
                <div class="kb-item-main">
                  <span class="kb-item-title">{{ a.title }}</span>
                  <span class="kb-item-cat">{{
                    knowledgeCategoryLabel(a.category)
                  }}</span>
                </div>
                <div class="kb-item-actions">
                  <button
                    type="button"
                    class="kb-icon-btn"
                    :title="a.active ? 'Disable' : 'Enable'"
                    @click.stop="toggleKnowledgeActive(a)"
                  >
                    {{ a.active ? "On" : "Off" }}
                  </button>
                  <button
                    type="button"
                    class="kb-icon-btn kb-icon-btn--danger"
                    title="Delete"
                    @click.stop="deleteKnowledgeArticle(a)"
                  >
                    ✕
                  </button>
                </div>
              </div>

              <button
                type="button"
                class="kb-add-btn"
                :disabled="knowledgeArticles.length >= knowledgeLimits.maxArticles"
                :title="
                  knowledgeArticles.length >= knowledgeLimits.maxArticles
                    ? 'Article limit reached'
                    : 'Add knowledge'
                "
                @click="openKnowledgeModal()"
              >
                + Add knowledge
              </button>
              <button
                type="button"
                class="kb-import-btn"
                :disabled="
                  kbImporting ||
                  knowledgeArticles.length >= knowledgeLimits.maxArticles
                "
                title="Import a .csv, .tsv, .txt, .json, .md or .log file"
                @click="triggerKnowledgeImport()"
              >
                {{ kbImporting ? "Importing…" : "↑ Import file" }}
              </button>
              <input
                ref="kbFileInput"
                type="file"
                accept=".csv,.tsv,.txt,.json,.md,.log"
                style="display: none"
                @change="onKnowledgeFileSelected"
              />
            </div>
          </div>
        </div>

        <!-- Bottom section -->
        <div class="sidebar-footer">
          <!-- Azure connect/status — always shown when not connected -->
          <div v-if="!azureConnected" class="azure-connect">
            <!-- Admin approval / consent error banner -->
            <div v-if="tenantError" class="tenant-error-banner">
              <svg
                width="14"
                height="14"
                viewBox="0 0 24 24"
                fill="none"
                stroke="#d13438"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
              >
                <circle cx="12" cy="12" r="10" />
                <line x1="12" y1="8" x2="12" y2="12" />
                <line x1="12" y1="16" x2="12.01" y2="16" />
              </svg>
              <span
                >Your home tenant blocked this app. Pick a tenant below or type
                one manually.</span
              >
            </div>
            <!-- Saved tenants — hidden -->
            <div v-if="false" class="saved-tenants">
              <span class="saved-tenants-label">Your tenants</span>
              <button
                v-for="t in savedTenants"
                :key="t.tenantId"
                class="saved-tenant-btn"
                @click="switchTenant(t.tenantId)"
                :title="t.tenantId"
              >
                <svg
                  width="11"
                  height="11"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                >
                  <path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
                  <polyline points="9 22 9 12 15 12 15 22" />
                </svg>
                {{
                  t.displayName ||
                  t.defaultDomain ||
                  t.tenantId.slice(0, 8) + "…"
                }}
              </button>
            </div>
            <!-- Manual tenant input -->
            <div class="tenant-input-area">
              <input
                v-model="tenantId"
                type="text"
                class="tenant-input"
                :class="{
                  'tenant-input--highlight':
                    tenantError && !savedTenants.length,
                }"
                placeholder="Tenant ID…"
              />
              <span v-if="!savedTenants.length" class="tenant-hint"
                >Leave empty to use your home tenant, or specify a different
                one</span
              >
            </div>
            <button
              class="azure-connect-btn"
              :disabled="authLoading === 'azure'"
              @click="startAuth('azure', '/auth/microsoft')"
            >
              <span
                v-if="authLoading === 'azure'"
                class="auth-spinner auth-spinner--azure"
              ></span>
              <svg
                v-else
                width="16"
                height="16"
                viewBox="0 0 21 21"
                fill="none"
              >
                <rect width="10" height="10" fill="#f25022" />
                <rect x="11" width="10" height="10" fill="#7fba00" />
                <rect y="11" width="10" height="10" fill="#00a4ef" />
                <rect x="11" y="11" width="10" height="10" fill="#ffb900" />
              </svg>
              {{
                authLoading === "azure"
                  ? "Connecting..."
                  : tenantId.trim()
                    ? "Connect to " + tenantId.trim()
                    : "Connect Azure"
              }}
            </button>
          </div>
          <div v-else-if="azureConnected" class="azure-status">
            <div class="azure-status-info">
              <svg width="14" height="14" viewBox="0 0 21 21" fill="none">
                <rect width="10" height="10" fill="#f25022" />
                <rect x="11" width="10" height="10" fill="#7fba00" />
                <rect y="11" width="10" height="10" fill="#00a4ef" />
                <rect x="11" y="11" width="10" height="10" fill="#ffb900" />
              </svg>
              <span class="azure-status-text">{{
                azureUserEmail || "Azure Connected"
              }}</span>
              <button
                class="azure-disconnect-btn"
                @click="disconnectAzure"
                title="Disconnect session (keeps Entra ID consent)"
              >
                <svg
                  width="12"
                  height="12"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                >
                  <line x1="18" y1="6" x2="6" y2="18" />
                  <line x1="6" y1="6" x2="18" y2="18" />
                </svg>
              </button>
            </div>
            <!-- Tenant switcher -->
            <div class="tenant-switcher" v-if="availableTenants.length > 1">
              <label
                class="tenant-switch-label"
                @click="showTenantSwitcher = !showTenantSwitcher"
              >
                <svg
                  width="11"
                  height="11"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                >
                  <path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
                  <polyline points="9 22 9 12 15 12 15 22" />
                </svg>
                Switch tenant ({{ availableTenants.length }})
                <svg
                  :class="['tenant-chevron', { open: showTenantSwitcher }]"
                  width="10"
                  height="10"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2.5"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                >
                  <polyline points="6 9 12 15 18 9" />
                </svg>
              </label>
              <div v-if="showTenantSwitcher" class="tenant-list">
                <button
                  v-for="t in availableTenants"
                  :key="t.tenantId"
                  class="tenant-list-item"
                  :class="{ active: t.tenantId === currentTenantId }"
                  :disabled="t.tenantId === currentTenantId"
                  @click="switchTenant(t.tenantId)"
                  :title="t.tenantId"
                >
                  <span class="tenant-list-name">{{
                    t.displayName || t.defaultDomain || t.tenantId
                  }}</span>
                  <span
                    v-if="t.tenantId === currentTenantId"
                    class="tenant-list-current"
                    >current</span
                  >
                  <span
                    v-if="t.defaultDomain && t.displayName"
                    class="tenant-list-domain"
                    >{{ t.defaultDomain }}</span
                  >
                </button>
              </div>
            </div>
            <!-- Incremental consent: one row per scope, all delegated, separate Entra ID consent each -->
            <!-- HIDDEN — single-button Connect Azure only.
                 Re-enable by removing v-if="false" to bring back License Optimization,
                 Cost Allocation, Log Analytics, Cost Exports add-on tiers. -->
            <div v-if="false" class="addons-section">
              <button
                class="addons-heading"
                type="button"
                @click="addonsOpen = !addonsOpen"
                :aria-expanded="addonsOpen"
              >
                <span class="addons-heading-text">
                  <span class="addons-title">Add scopes</span>
                  <span class="addons-sub">· Delegated &amp; read-only</span>
                </span>
                <svg
                  class="addons-heading-chevron"
                  :class="{ open: addonsOpen }"
                  width="14"
                  height="14"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2.5"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  aria-hidden="true"
                >
                  <polyline points="6 9 12 15 18 9" />
                </svg>
              </button>

              <div class="addons-body-wrap" :class="{ open: addonsOpen }">
                <div class="addons-body">
                  <div
                    class="scope-row"
                    :class="{
                      'scope-row--active': licensesEnabled,
                      'scope-row--glow': glowingRow === 0,
                      'scope-row--open': addonRowsOpen[0],
                    }"
                  >
                    <button
                      class="scope-row-summary"
                      type="button"
                      @click="clickScopeRow(0)"
                      title="Show details"
                    >
                      <span v-if="licensesEnabled" class="scope-row-mark"
                        >✓</span
                      >
                      <span class="scope-row-title">License Optimization</span>
                      <span
                        class="scope-row-chevron"
                        @click.stop="addonRowsOpen[0] = !addonRowsOpen[0]"
                        title="Show details"
                      >
                        <svg
                          width="14"
                          height="14"
                          viewBox="0 0 24 24"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="2.5"
                          stroke-linecap="round"
                          stroke-linejoin="round"
                        >
                          <polyline points="6 9 12 15 18 9" />
                        </svg>
                      </span>
                    </button>
                    <div class="scope-row-detail-wrap">
                      <div class="scope-row-detail">
                        <p class="scope-row-desc">
                          Read M365 license inventory &amp; Copilot adoption
                        </p>
                        <div class="scope-row-meta">
                          <span class="scope-badge scope-badge--delegated"
                            >👤 Delegated</span
                          >
                          <span class="scope-badge">Microsoft Graph</span>
                        </div>
                        <p class="scope-row-perms">
                          Organization.Read.All · Reports.Read.All
                        </p>
                        <button
                          v-if="!licensesEnabled"
                          class="scope-row-add"
                          @click.stop="
                            startAuth('azure', '/auth/microsoft?tier=licenses')
                          "
                        >
                          Add scope
                        </button>
                        <span v-else class="scope-row-status">✓ Consented</span>
                      </div>
                    </div>
                  </div>

                  <div
                    class="scope-row"
                    :class="{
                      'scope-row--active': chargebackEnabled,
                      'scope-row--glow': glowingRow === 1,
                      'scope-row--open': addonRowsOpen[1],
                    }"
                  >
                    <button
                      class="scope-row-summary"
                      type="button"
                      @click="clickScopeRow(1)"
                      title="Show details"
                    >
                      <span v-if="chargebackEnabled" class="scope-row-mark"
                        >✓</span
                      >
                      <span class="scope-row-title"
                        >Cost Allocation &amp; Chargeback</span
                      >
                      <span
                        class="scope-row-chevron"
                        @click.stop="addonRowsOpen[1] = !addonRowsOpen[1]"
                        title="Show details"
                      >
                        <svg
                          width="14"
                          height="14"
                          viewBox="0 0 24 24"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="2.5"
                          stroke-linecap="round"
                          stroke-linejoin="round"
                        >
                          <polyline points="6 9 12 15 18 9" />
                        </svg>
                      </span>
                    </button>
                    <div class="scope-row-detail-wrap">
                      <div class="scope-row-detail">
                        <p class="scope-row-desc">
                          Map Azure costs to users, teams &amp; cost centers
                        </p>
                        <div class="scope-row-meta">
                          <span class="scope-badge scope-badge--delegated"
                            >👤 Delegated</span
                          >
                          <span class="scope-badge">Microsoft Graph</span>
                        </div>
                        <p class="scope-row-perms">
                          User.Read.All · Group.Read.All
                        </p>
                        <button
                          v-if="!chargebackEnabled"
                          class="scope-row-add"
                          @click.stop="
                            startAuth(
                              'azure',
                              '/auth/microsoft?tier=chargeback',
                            )
                          "
                        >
                          Add scope
                        </button>
                        <span v-else class="scope-row-status">✓ Consented</span>
                      </div>
                    </div>
                  </div>

                  <div
                    class="scope-row"
                    :class="{
                      'scope-row--active': logAnalyticsEnabled,
                      'scope-row--glow': glowingRow === 2,
                      'scope-row--open': addonRowsOpen[2],
                    }"
                  >
                    <button
                      class="scope-row-summary"
                      type="button"
                      @click="clickScopeRow(2)"
                      title="Show details"
                    >
                      <span v-if="logAnalyticsEnabled" class="scope-row-mark"
                        >✓</span
                      >
                      <span class="scope-row-title"
                        >Log Analytics Deep Dives</span
                      >
                      <span
                        class="scope-row-chevron"
                        @click.stop="addonRowsOpen[2] = !addonRowsOpen[2]"
                        title="Show details"
                      >
                        <svg
                          width="14"
                          height="14"
                          viewBox="0 0 24 24"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="2.5"
                          stroke-linecap="round"
                          stroke-linejoin="round"
                        >
                          <polyline points="6 9 12 15 18 9" />
                        </svg>
                      </span>
                    </button>
                    <div class="scope-row-detail-wrap">
                      <div class="scope-row-detail">
                        <p class="scope-row-desc">
                          Run KQL for unit economics &amp; ingestion cost
                          analysis
                        </p>
                        <div class="scope-row-meta">
                          <span class="scope-badge scope-badge--delegated"
                            >👤 Delegated</span
                          >
                          <span class="scope-badge">Log Analytics API</span>
                        </div>
                        <p class="scope-row-perms">Data.Read</p>
                        <button
                          v-if="!logAnalyticsEnabled"
                          class="scope-row-add"
                          @click.stop="
                            startAuth(
                              'azure',
                              '/auth/microsoft?tier=loganalytics',
                            )
                          "
                        >
                          Add scope
                        </button>
                        <span v-else class="scope-row-status">✓ Consented</span>
                      </div>
                    </div>
                  </div>

                  <div
                    class="scope-row"
                    :class="{
                      'scope-row--active': storageEnabled,
                      'scope-row--glow': glowingRow === 3,
                      'scope-row--open': addonRowsOpen[3],
                    }"
                  >
                    <button
                      class="scope-row-summary"
                      type="button"
                      @click="clickScopeRow(3)"
                      title="Show details"
                    >
                      <span v-if="storageEnabled" class="scope-row-mark"
                        >✓</span
                      >
                      <span class="scope-row-title">Cost Exports</span>
                      <span
                        class="scope-row-chevron"
                        @click.stop="addonRowsOpen[3] = !addonRowsOpen[3]"
                        title="Show details"
                      >
                        <svg
                          width="14"
                          height="14"
                          viewBox="0 0 24 24"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="2.5"
                          stroke-linecap="round"
                          stroke-linejoin="round"
                        >
                          <polyline points="6 9 12 15 18 9" />
                        </svg>
                      </span>
                    </button>
                    <div class="scope-row-detail-wrap">
                      <div class="scope-row-detail">
                        <p class="scope-row-desc">
                          Read Cost Management export files from your Storage
                          Account
                        </p>
                        <div class="scope-row-meta">
                          <span class="scope-badge scope-badge--delegated"
                            >👤 Delegated</span
                          >
                          <span class="scope-badge">Azure Storage</span>
                        </div>
                        <p class="scope-row-perms">user_impersonation</p>
                        <button
                          v-if="!storageEnabled"
                          class="scope-row-add"
                          @click.stop="
                            startAuth('azure', '/auth/microsoft?tier=storage')
                          "
                        >
                          Add scope
                        </button>
                        <span v-if="storageEnabled" class="scope-row-status"
                          >✓ Consented</span
                        >
                      </div>
                    </div>
                  </div>

                  <div class="addons-divider"></div>

                  <button
                    v-if="
                      !(
                        licensesEnabled &&
                        chargebackEnabled &&
                        logAnalyticsEnabled &&
                        storageEnabled
                      )
                    "
                    class="scope-grant-all"
                    @click="startAuth('azure', '/auth/microsoft/adminconsent')"
                    title="Tenant admins (Global Admin / Privileged Role Admin): one click consents all 4 scopes for every user in your tenant. Non-admins will see a 'You need admin approval' message — use the individual scope buttons above instead."
                  >
                    <span class="scope-grant-all-icon">🛡</span>
                    <span class="scope-grant-all-body">
                      <span class="scope-grant-all-title"
                        >Grant for whole tenant
                        <span class="scope-grant-all-tag">admin</span></span
                      >
                      <span class="scope-grant-all-desc"
                        >Requires Global Admin · 1 click consents all 4 scopes
                        for every user.</span
                      >
                    </span>
                  </button>
                </div>
              </div>

              <!-- HIDDEN — keep UI to email + X disconnect only.
                   Re-enable by removing v-if="false" to bring back the revoke-all button. -->
              <button
                v-if="false"
                class="azure-revoke-btn"
                @click="revokeAllPermissions"
                title="Disconnect and revoke all Entra ID permissions for this app"
              >
                Revoke all permissions
              </button>
            </div>
          </div>
        </div>
      </aside>

      <!-- Center: chat area -->
      <div
        class="chat-main"
        @dragenter="onDragEnter"
        @dragover="onDragOver"
        @dragleave="onDragLeave"
        @drop="onDrop"
      >
        <!-- Messages -->
        <div class="messages" ref="messagesEl">
          <div class="messages-inner">
            <div v-if="messages.length === 0" class="empty-state">
              <div class="hero">
                <h1 class="hero-title">
                  Azure <span class="hero-title-accent">FinOps</span> Agent
                </h1>
                <p class="hero-tagline">
                  Weeks of FinOps work. Done in minutes.
                </p>
                <div class="hero-cards">
                  <div class="hero-card">
                    <div class="hero-card-title">Quantified savings</div>
                    <div class="hero-card-desc">
                      Reservations. Savings Plans. Hybrid Benefit. Rightsizing.
                      Idle &amp; orphaned — ranked by annual $ impact.
                    </div>
                  </div>
                  <div class="hero-card">
                    <div class="hero-card-title">Maturity score</div>
                    <div class="hero-card-desc">
                      FinOps Foundation Crawl / Walk / Run. Scored 0–5 per
                      capability. The consultant assessment, in a chat.
                    </div>
                  </div>
                  <div class="hero-card">
                    <div class="hero-card-title">Agentic remediation</div>
                    <div class="hero-card-desc">
                      Applies tags. Sets budgets. Drafts cleanup scripts. Never
                      deletes.
                    </div>
                  </div>
                  <div class="hero-card">
                    <div class="hero-card-title">Spots cost spikes</div>
                    <div class="hero-card-desc">
                      Catches anomalies the moment they happen. Explains the
                      why. Tells you who to bill.
                    </div>
                  </div>
                  <div class="hero-card">
                    <div class="hero-card-title">License &amp; Copilot ROI</div>
                    <div class="hero-card-desc">
                      Microsoft Graph surfaces unused M365 licenses, idle
                      Copilot seats, SKU mismatches — savings other tools miss.
                    </div>
                  </div>
                  <div class="hero-card">
                    <div class="hero-card-title">CFO-ready decks</div>
                    <div class="hero-card-desc">
                      20+ inline charts. One-click branded HTML deck. Walk in
                      with the presentation already built.
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <!-- Message list -->
            <div
              v-for="(msg, i) in messages"
              :key="i"
              class="message-row"
              :class="
                msg.role === 'user' ? 'message-row--user' : 'message-row--ai'
              "
            >
              <div v-if="msg.role === 'user'" class="bubble bubble--user">
                {{ msg.content }}
              </div>
              <div v-else class="ai-row">
                <div class="ai-header">
                  <div class="ai-avatar">AI</div>
                </div>
                <div class="ai-content">
                  <div
                    v-for="(chart, ci) in msg.charts || []"
                    :key="'chart-' + i + '-' + ci"
                    class="chart-container"
                    :ref="(el) => el && mountChart(el, chart)"
                  ></div>
                  <div
                    class="message-text"
                    v-html="renderContent(msg.content)"
                  ></div>
                  <div v-if="msg.followUp" class="follow-up-buttons">
                    <button
                      v-for="(a, ai) in msg.followUp.actions &&
                      msg.followUp.actions.length
                        ? msg.followUp.actions
                        : [msg.followUp]"
                      :key="ai"
                      class="follow-up-btn"
                      @click="sendQuestion(a.prompt)"
                    >
                      {{ a.label }}
                    </button>
                  </div>
                  <div v-if="msg.html" class="html-deck-card">
                    <div class="html-deck-card-icon">
                      <svg
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        stroke-width="1.8"
                        stroke-linecap="round"
                        stroke-linejoin="round"
                      >
                        <rect x="3" y="4" width="18" height="14" rx="2" />
                        <line x1="8" y1="21" x2="16" y2="21" />
                        <line x1="12" y1="18" x2="12" y2="21" />
                      </svg>
                    </div>
                    <div class="html-deck-card-body">
                      <div class="html-deck-card-title">
                        {{ htmlCardTitle(msg.html.slideCount) }}
                      </div>
                      <div class="html-deck-card-meta">
                        {{ htmlCardMeta(msg.html.slideCount) }}<span
                          v-if="msg.html.createdAt"
                        >
                          · {{ msg.html.createdAt }}</span
                        >
                      </div>
                    </div>
                    <a
                      :href="'/api/download/html/' + msg.html.fileId"
                      :download="msg.html.fileName"
                      class="html-deck-card-btn"
                      >Download</a
                    >
                  </div>
                  <div v-if="msg.script" class="script-inline-block">
                    <div class="script-header">
                      <div class="script-header-left">
                        <svg
                          width="14"
                          height="14"
                          viewBox="0 0 24 24"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="2"
                          stroke-linecap="round"
                          stroke-linejoin="round"
                        >
                          <polyline points="16 18 22 12 16 6" />
                          <polyline points="8 6 2 12 8 18" />
                        </svg>
                        <span class="script-filename">{{
                          msg.script.fileName
                        }}</span>
                        <span class="script-meta"
                          >{{ msg.script.lineCount }} lines &middot;
                          {{
                            msg.script.language === "powershell"
                              ? "PowerShell"
                              : "Bash"
                          }}</span
                        >
                      </div>
                      <div class="script-header-actions">
                        <button
                          class="script-copy-btn"
                          @click="copyScript(msg.script.content)"
                          :title="'Copy to clipboard'"
                        >
                          <svg
                            width="14"
                            height="14"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            stroke-width="2"
                            stroke-linecap="round"
                            stroke-linejoin="round"
                          >
                            <rect
                              x="9"
                              y="9"
                              width="13"
                              height="13"
                              rx="2"
                              ry="2"
                            />
                            <path
                              d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"
                            />
                          </svg>
                        </button>
                        <a
                          :href="'/api/download/script/' + msg.script.fileId"
                          class="script-download-btn"
                          download
                        >
                          <svg
                            width="14"
                            height="14"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            stroke-width="2"
                            stroke-linecap="round"
                            stroke-linejoin="round"
                          >
                            <path
                              d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"
                            />
                            <polyline points="7 10 12 15 17 10" />
                            <line x1="12" y1="15" x2="12" y2="3" />
                          </svg>
                          Download
                        </a>
                      </div>
                    </div>
                    <div
                      class="script-description"
                      v-if="msg.script.description"
                    >
                      {{ msg.script.description }}
                    </div>
                    <button
                      class="script-toggle-btn"
                      @click="msg.script.expanded = !msg.script.expanded"
                    >
                      {{ msg.script.expanded ? "Hide script" : "View script" }}
                    </button>
                    <pre
                      v-if="msg.script.expanded"
                      class="script-code"
                    ><code>{{ msg.script.content }}</code></pre>
                  </div>
                </div>
              </div>
            </div>

            <!-- Streaming indicator -->
            <div v-if="streaming" class="message-row message-row--ai">
              <div class="ai-row">
                <div class="ai-header">
                  <div class="ai-avatar">AI</div>
                  <span v-if="streamIntent" class="stream-intent">
                    {{ streamIntent }}
                  </span>
                </div>
                <div class="ai-content">
                  <div
                    v-for="(chart, ci) in streamCharts"
                    :key="'stream-chart-' + ci"
                    class="chart-container"
                    :ref="(el) => el && mountChart(el, chart)"
                  ></div>
                  <div class="message-text" v-if="streamBuffer">
                    <span v-html="renderContent(streamBuffer)"></span>
                    <span class="streaming-cursor"></span>
                  </div>
                  <div class="message-text" v-else-if="!streamIntent">
                    <span class="streaming-cursor"></span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- HTML deck (streaming) — compact card -->
        <div v-if="htmlReady" class="html-deck-card">
          <div class="html-deck-card-icon">
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <rect x="3" y="4" width="18" height="14" rx="2" />
              <line x1="8" y1="21" x2="16" y2="21" />
              <line x1="12" y1="18" x2="12" y2="21" />
            </svg>
          </div>
          <div class="html-deck-card-body">
            <div class="html-deck-card-title">{{ htmlCardTitle(htmlReady.slideCount) }}</div>
            <div class="html-deck-card-meta">
              {{ htmlCardMeta(htmlReady.slideCount) }}<span v-if="htmlReady.createdAt">
                · {{ htmlReady.createdAt }}</span
              >
            </div>
          </div>
          <a
            :href="'/api/download/html/' + htmlReady.fileId"
            :download="htmlReady.fileName"
            class="html-deck-card-btn"
            >Download</a
          >
        </div>

        <!-- Script download (streaming) -->
        <div v-if="scriptReady" class="script-download-bar">
          <div class="script-inline-block">
            <div class="script-header">
              <div class="script-header-left">
                <svg
                  width="14"
                  height="14"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                >
                  <polyline points="16 18 22 12 16 6" />
                  <polyline points="8 6 2 12 8 18" />
                </svg>
                <span class="script-filename">{{ scriptReady.fileName }}</span>
                <span class="script-meta"
                  >{{ scriptReady.lineCount }} lines &middot;
                  {{
                    scriptReady.language === "powershell"
                      ? "PowerShell"
                      : "Bash"
                  }}</span
                >
              </div>
              <div class="script-header-actions">
                <button
                  class="script-copy-btn"
                  @click="copyScript(scriptReady.content)"
                  title="Copy to clipboard"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <rect x="9" y="9" width="13" height="13" rx="2" ry="2" />
                    <path
                      d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"
                    />
                  </svg>
                </button>
                <a
                  :href="'/api/download/script/' + scriptReady.fileId"
                  class="script-download-btn"
                  download
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                    <polyline points="7 10 12 15 17 10" />
                    <line x1="12" y1="15" x2="12" y2="3" />
                  </svg>
                  Download
                </a>
              </div>
            </div>
            <div class="script-description" v-if="scriptReady.description">
              {{ scriptReady.description }}
            </div>
            <pre
              class="script-code"
            ><code>{{ scriptReady.content }}</code></pre>
          </div>
        </div>

        <!-- Mobile auth bar (hidden on desktop, shown on mobile) -->
        <div class="mobile-auth-bar"></div>

        <!-- Drag-drop overlay -->
        <div
          v-if="dragActive"
          class="drop-overlay"
          @dragenter.prevent
          @dragover.prevent
          @dragleave.prevent="onDragLeave"
          @drop.prevent.stop="onDrop"
        >
          <div class="drop-overlay-card">
            <svg
              width="48"
              height="48"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
              <polyline points="17 8 12 3 7 8" />
              <line x1="12" y1="3" x2="12" y2="15" />
            </svg>
            <div class="drop-overlay-title">Drop to attach</div>
            <div class="drop-overlay-sub">
              CSV · TSV · JSON · TXT · XLSX · PDF · Parquet (≤ 100 MB)
            </div>
          </div>
        </div>

        <!-- Knowledge editor modal -->
        <div
          v-if="showKnowledgeModal"
          class="kb-modal-overlay"
          @click.self="closeKnowledgeModal"
        >
          <div class="kb-modal" role="dialog" aria-modal="true">
            <div class="kb-modal-head">
              <span class="kb-modal-title">{{
                kbForm.id ? "Edit knowledge" : "Add knowledge"
              }}</span>
              <button
                type="button"
                class="kb-modal-close"
                title="Close"
                @click="closeKnowledgeModal"
              >
                ✕
              </button>
            </div>

            <div class="kb-modal-body">
              <label class="kb-field-label" for="kb-title">Title</label>
              <input
                id="kb-title"
                v-model="kbForm.title"
                type="text"
                class="kb-input"
                :maxlength="120"
                placeholder="e.g. Production subscription map"
              />

              <label class="kb-field-label" for="kb-category">Category</label>
              <select
                id="kb-category"
                v-model="kbForm.category"
                class="kb-input"
              >
                <option
                  v-for="c in knowledgeCategories"
                  :key="c"
                  :value="c"
                >
                  {{ knowledgeCategoryLabel(c) }}
                </option>
              </select>

              <label class="kb-field-label" for="kb-content"
                >Content
                <span class="kb-char-count"
                  >{{ kbForm.content.length }} /
                  {{ knowledgeLimits.maxArticleChars }}</span
                ></label
              >
              <textarea
                id="kb-content"
                v-model="kbForm.content"
                class="kb-textarea"
                :maxlength="knowledgeLimits.maxArticleChars"
                rows="12"
                placeholder="Markdown is supported. Be concise — this is sent to the model as context. See the authoring guide for examples."
              ></textarea>

              <p v-if="kbError" class="kb-modal-error">{{ kbError }}</p>
            </div>

            <div class="kb-modal-foot">
              <a
                href="https://github.com/Azure-Samples/azure-finops-agent/blob/main/docs/knowledge-authoring-guide.md"
                target="_blank"
                rel="noopener"
                class="kb-help-link kb-modal-help"
                >Authoring guide →</a
              >
              <div class="kb-modal-foot-actions">
                <button
                  type="button"
                  class="kb-btn kb-btn--ghost"
                  @click="closeKnowledgeModal"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  class="kb-btn kb-btn--primary"
                  :disabled="kbSaving || !kbForm.title.trim() || !kbForm.content.trim()"
                  @click="saveKnowledgeArticle"
                >
                  {{ kbSaving ? "Saving…" : "Save" }}
                </button>
              </div>
            </div>
          </div>
        </div>

        <!-- Input bar -->
        <div class="input-area">
          <!-- (Removed) Analyze CTA above input — replaced by the Analyze button inside the input bar. -->

          <div
            class="input-wrapper"
            :class="{ 'input-wrapper--disabled': false }"
          >
            <!-- Attachment chips -->
            <div v-if="attachments.length" class="attachment-chips">
              <div
                v-for="att in attachments"
                :key="att.uid"
                class="attachment-chip"
                :class="{
                  'attachment-chip--err': att.error,
                  'attachment-chip--up': att.uploading,
                }"
                :title="
                  att.error || `${att.kind} · ${formatBytes(att.sizeBytes)}`
                "
              >
                <svg
                  width="12"
                  height="12"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                >
                  <path
                    d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"
                  />
                  <polyline points="14 2 14 8 20 8" />
                </svg>
                <span class="attachment-chip-name">{{ att.fileName }}</span>
                <span v-if="att.uploading" class="attachment-chip-meta"
                  >uploading…</span
                >
                <span v-else-if="att.error" class="attachment-chip-meta"
                  >failed</span
                >
                <span v-else class="attachment-chip-meta">{{
                  formatBytes(att.sizeBytes)
                }}</span>
                <button
                  class="attachment-chip-x"
                  @click="removeAttachment(att)"
                  title="Remove"
                >
                  ✕
                </button>
              </div>
            </div>

            <!-- One-click Analyze removed from here — now sits above input wrapper -->

            <textarea
              ref="inputEl"
              v-model="input"
              rows="1"
              @keydown.enter.exact.prevent="send"
              @input="autoGrowInput"
              placeholder="Ask a question about your data"
              class="input-field"
              :disabled="!user"
            ></textarea>
            <div class="input-bottom-bar">
              <div class="input-bottom-left">
                <input
                  ref="fileInputEl"
                  type="file"
                  multiple
                  accept=".csv,.tsv,.json,.txt,.log,.md,.xlsx,.xls,.pdf,.parquet"
                  style="display: none"
                  @change="onFilePicked"
                />
                <button
                  class="input-action-btn"
                  :disabled="streaming"
                  @click="openFilePicker"
                  title="Attach file (CSV, JSON, TXT, XLSX, PDF, Parquet)"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <path
                      d="m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"
                    />
                  </svg>
                  <span>Attach</span>
                </button>
                <button
                  class="input-action-btn"
                  :disabled="messages.length === 0 || streaming"
                  @click="clearMessages()"
                  title="Clear chat"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <path
                      d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"
                    />
                    <path d="M3 3v5h5" />
                  </svg>
                  <span>Clear</span>
                </button>
                <button
                  v-if="attachments.length > 0"
                  class="input-action-btn"
                  :disabled="streaming"
                  @click="requestAnalyze()"
                  title="Find biggest cost waste and recommend actions"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <circle cx="11" cy="11" r="7" />
                    <line x1="21" y1="21" x2="16.5" y2="16.5" />
                  </svg>
                  <span>Analyze</span>
                </button>
                <button
                  class="input-action-btn"
                  :disabled="messages.length < 2 || streaming"
                  @click="requestPresentation()"
                  title="Generate Presentation"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <rect x="3" y="4" width="18" height="14" rx="2" />
                    <line x1="8" y1="21" x2="16" y2="21" />
                    <line x1="12" y1="18" x2="12" y2="21" />
                  </svg>
                  <span>Presentation</span>
                </button>
                <button
                  class="input-action-btn"
                  :disabled="messages.length < 2 || streaming"
                  @click="requestScript()"
                  title="Generate Script"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <polyline points="16 18 22 12 16 6" />
                    <polyline points="8 6 2 12 8 18" />
                  </svg>
                  <span>Script</span>
                </button>
              </div>
              <div class="input-bottom-right">
                <button
                  v-if="streaming"
                  class="action-btn action-btn--stop"
                  @click="stopGeneration"
                >
                  <svg
                    xmlns="http://www.w3.org/2000/svg"
                    width="16"
                    height="16"
                    viewBox="0 0 24 24"
                    fill="currentColor"
                  >
                    <rect x="6" y="6" width="12" height="12" rx="2" />
                  </svg>
                </button>
                <button
                  v-else
                  class="action-btn"
                  :class="
                    input.trim() ? 'action-btn--active' : 'action-btn--disabled'
                  "
                  :disabled="!input.trim()"
                  @click="send"
                >
                  <svg
                    xmlns="http://www.w3.org/2000/svg"
                    width="16"
                    height="16"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2.5"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <line x1="12" y1="19" x2="12" y2="5" />
                    <polyline points="5 12 12 5 19 12" />
                  </svg>
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- Right sidebar: Agent (tool calls) + Sessions (Entra-only) -->
      <aside
        class="tools-sidebar"
        :class="{
          'tools-sidebar--open':
            allToolCalls.length > 0 || streaming || azureConnected,
        }"
      >
        <!-- ── Top half: Agent ── -->
        <div class="tools-sidebar-pane tools-sidebar-pane--agent">
          <div class="tools-sidebar-header">
            <div class="tools-sidebar-header-text">
              <span class="tools-sidebar-title">Agent</span>
              <span class="tools-sidebar-status">
                <span
                  class="tools-sidebar-status-dot"
                  :class="{ 'tools-sidebar-status-dot--live': streaming }"
                ></span>
                <span class="tools-sidebar-status-text">{{ agentStatus }}</span>
              </span>
            </div>
            <span class="st-count">{{ allToolCalls.length }}</span>
          </div>
          <div class="tools-sidebar-scroll">
            <template v-for="tc in reversedToolCalls" :key="tc._uid">
              <!-- Ghost cooling-down row: animated, expand-to-detail, ephemeral -->
              <div
                v-if="tc._isCooler"
                :class="['st-row', 'st-row--cooler', { 'st-row--cooler-open': tc.expanded }]"
                @click.stop="tc.expanded = !tc.expanded"
                :title="`Cooling down ${tc.tool} (HTTP ${tc.status}) attempt ${tc.attempt}, waiting ${Math.round(tc.wait)}s`"
              >
                <svg class="st-icon st-icon--cooler" viewBox="0 0 16 16" fill="none">
                  <circle cx="8" cy="8" r="6" stroke="currentColor" stroke-width="2" stroke-dasharray="6 4">
                    <animateTransform attributeName="transform" type="rotate" from="0 8 8" to="360 8 8" dur="1.4s" repeatCount="indefinite"/>
                  </circle>
                </svg>
                <span class="st-name">Cooling down… {{ Math.round(tc.wait) }}s (attempt {{ tc.attempt }})</span>
                <span class="st-time">HTTP {{ tc.status }}</span>
                <div v-if="tc.expanded" class="st-cooler-detail" @click.stop>
                  <div class="st-cooler-row"><strong>Tool:</strong> {{ tc.tool }}</div>
                  <div class="st-cooler-row"><strong>Status:</strong> HTTP {{ tc.status }}</div>
                  <div class="st-cooler-row"><strong>Attempt:</strong> {{ tc.attempt }} of 5</div>
                  <div class="st-cooler-row"><strong>Wait:</strong> {{ Math.round(tc.wait) }}s</div>
                  <div class="st-cooler-row st-cooler-url"><strong>URL:</strong> <code>{{ tc.url }}</code></div>
                </div>
              </div>
              <!-- Normal tool row -->
              <div
                v-else
                :class="[
                  'st-row',
                  { 'st-row--running': !tc.done, 'st-row--clickable': tc.done },
                ]"
                @click.stop="
                  tc.done && (hoveredTool = hoveredTool === tc ? null : tc)
                "
              >
              <svg
                v-if="!tc.done"
                class="st-icon st-icon--spin"
                viewBox="0 0 16 16"
                fill="none"
              >
                <circle
                  cx="8"
                  cy="8"
                  r="6"
                  stroke="#bf8700"
                  stroke-width="2"
                  stroke-dasharray="28"
                  stroke-dashoffset="8"
                  stroke-linecap="round"
                />
              </svg>
              <svg
                v-else-if="tc.success"
                class="st-icon st-icon--ok"
                viewBox="0 0 16 16"
                fill="none"
              >
                <circle
                  cx="8"
                  cy="8"
                  r="7"
                  stroke="#1a7f37"
                  stroke-width="1.5"
                />
                <path
                  d="M5 8.2 7 10.2 11 6"
                  stroke="#1a7f37"
                  stroke-width="1.5"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                />
              </svg>
              <svg
                v-else
                class="st-icon st-icon--fail"
                viewBox="0 0 16 16"
                fill="none"
              >
                <circle
                  cx="8"
                  cy="8"
                  r="7"
                  stroke="#cf222e"
                  stroke-width="1.5"
                />
                <path
                  d="M5.5 5.5 10.5 10.5M10.5 5.5 5.5 10.5"
                  stroke="#cf222e"
                  stroke-width="1.5"
                  stroke-linecap="round"
                />
              </svg>
              <span class="st-name">{{ friendlyToolLabel(tc) }}</span>
              <span v-if="tc.done" class="st-time">{{
                formatDuration(tc.durationMs)
              }}</span>
            </div>
            </template>
          </div>
        </div>
        <!-- ── Bottom half: Conversations (works for anonymous + signed-in) ── -->
        <div
          class="tools-sidebar-pane tools-sidebar-pane--sessions"
        >
          <div class="tools-sidebar-header sessions-header">
            <div class="tools-sidebar-header-text">
              <span class="tools-sidebar-title">Conversations</span>
              <span class="tools-sidebar-status">
                <span class="tools-sidebar-status-text"
                  >{{ sessions.length }} saved</span
                >
              </span>
            </div>
            <button
              class="sessions-new-btn"
              :disabled="clearing"
              @click="newSession"
              title="Start a new conversation"
            >
              + New
            </button>
          </div>
          <div class="tools-sidebar-scroll sessions-scroll">
            <div v-if="sessions.length === 0" class="sessions-empty">
              No saved conversations yet — chat to create one.
            </div>
            <div
              v-for="s in sessions"
              :key="s.id"
              :class="[
                'session-row',
                { 'session-row--current': s.id === currentSessionId },
                { 'session-row--running': runningSessions.has(s.id) },
              ]"
              @click="selectSession(s.id)"
              :title="s.summary"
            >
              <span
                class="tools-sidebar-status-dot"
                :class="{
                  'tools-sidebar-status-dot--live': runningSessions.has(s.id),
                }"
                :aria-label="
                  runningSessions.has(s.id) ? 'Conversation is running' : 'Idle'
                "
                :title="
                  runningSessions.has(s.id)
                    ? 'This conversation is still running'
                    : ''
                "
              ></span>
              <div class="session-row-main">
                <span class="session-row-title">{{
                  s.summary || "Untitled conversation"
                }}</span>
                <span class="session-row-time">{{
                  formatRelativeTime(s.modified)
                }}</span>
              </div>
              <button
                class="session-row-delete"
                @click.stop="deleteSession(s.id)"
                title="Delete this conversation"
                aria-label="Delete conversation"
              >
                ×
              </button>
            </div>
          </div>
        </div>
      </aside>
    </div>
    <!-- end portal-body -->

    <!-- Tool popover -->
    <Teleport to="body">
      <div v-if="hoveredTool" class="tool-popover" @click.stop>
        <div class="tool-popover-header">
          <span class="tool-popover-name">{{
            friendlyToolLabel(hoveredTool)
          }}</span>
          <span class="tool-popover-time">{{
            formatDuration(hoveredTool.durationMs)
          }}</span>
          <button class="tool-popover-close" @click="hoveredTool = null">
            &times;
          </button>
        </div>
        <div class="tool-popover-body">
          <div v-if="hoveredTool.args" class="tool-popover-section">
            <div class="tool-popover-label">INPUT</div>
            <pre
              class="tool-popover-pre hljs"
            ><code class="language-json" v-html="highlightJson(hoveredTool.args)"></code></pre>
          </div>
          <div v-if="hoveredTool.result" class="tool-popover-section">
            <div class="tool-popover-label">RESPONSE</div>
            <pre
              class="tool-popover-pre hljs"
            ><code class="language-json" v-html="highlightJson(truncate(hoveredTool.result, 4000))"></code></pre>
          </div>
          <div v-if="hoveredTool.error" class="tool-popover-section">
            <div class="tool-popover-label">ERROR</div>
            <pre class="tool-popover-pre tool-popover-pre--error">{{
              hoveredTool.error
            }}</pre>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>

<script setup>
import * as echarts from "echarts";
import hljs from "highlight.js/lib/core";
import hljsJson from "highlight.js/lib/languages/json";
import "highlight.js/styles/github-dark.css";
import {
    computed,
    nextTick,
    onBeforeUnmount,
    onMounted,
    reactive,
    ref,
    watch,
} from "vue";
import {
    maturityCategories,
    pricingCategory,
} from "../data/sidebarCategories.js";
hljs.registerLanguage("json", hljsJson);

const props = defineProps({
  user: { type: Object, default: null },
});
const emit = defineEmits(["logout", "login"]);

const messages = ref([]);
const input = ref("");
// Sessions that currently have an in-flight stream — drives the small pulsing
// dot on each row in the Conversations sidebar so the user can tell at a
// glance which conversation is still working. Sentinel "__pending__" is used
// while a brand-new session is waiting for the backend to assign its id.
const runningSessions = reactive(new Set());
// `streaming` is derived: true iff the currently-viewed session has an
// in-flight stream. This lets the user navigate to a different session
// while another keeps running in the background — the input box is only
// disabled for the session that is actually busy.
const streaming = computed(() =>
  runningSessions.has(currentSessionId.value || "__pending__"),
);
const streamBuffer = ref("");
const activeTools = ref([]);
// Per-session live-stream tool calls / charts. Keyed by sessionId (or the
// "__pending__" sentinel before the server echoes the real id). This keeps
// each in-flight stream's UI state isolated so switching between concurrently
// streaming sessions never blanks out the right-rail tool list. The visible
// streamToolCalls / streamCharts are computeds derived from these maps for
// whichever session the user is currently viewing.
const perSessionToolCalls = reactive(new Map());
const perSessionCharts = reactive(new Map());
// Per-session ephemeral "cooling down" ghost rows. NOT persisted into
// messages/IndexedDB — they vanish at stream end. Each entry:
// { _uid, _isCooler:true, tool, url, attempt, wait, status, ts, expanded, _timer }
const perSessionCoolers = reactive(new Map());
const streamToolCalls = computed(
  () => perSessionToolCalls.get(currentSessionId.value || "__pending__") || [],
);
const streamCharts = computed(
  () => perSessionCharts.get(currentSessionId.value || "__pending__") || [],
);
const streamCoolers = computed(
  () => perSessionCoolers.get(currentSessionId.value || "__pending__") || [],
);
const streamFollowUp = ref(null);
const streamIntent = ref("");
const htmlReady = ref(null);
const scriptReady = ref(null);
const messagesEl = ref(null);
const inputEl = ref(null);
const chartInstances = [];
let intentAnimTimer = null;

// ── Uploaded attachments ────────────────────────────────────────────
const attachments = ref([]); // [{ fileId, fileName, kind, sizeBytes, uploading?, error? }]
const dragActive = ref(false);
const fileInputEl = ref(null);
let dragCounter = 0;

function openFilePicker() {
  fileInputEl.value?.click();
}

async function onFilePicked(ev) {
  const files = Array.from(ev.target.files || []);
  await uploadFiles(files);
  if (fileInputEl.value) fileInputEl.value.value = "";
}

function onDragEnter(ev) {
  ev.preventDefault();
  if (!ev.dataTransfer?.types?.includes("Files")) return;
  dragCounter++;
  dragActive.value = true;
}
function onDragLeave(ev) {
  ev.preventDefault();
  dragCounter = Math.max(0, dragCounter - 1);
  if (dragCounter === 0) dragActive.value = false;
}
function onDragOver(ev) {
  if (ev.dataTransfer?.types?.includes("Files")) ev.preventDefault();
}
async function onDrop(ev) {
  ev.preventDefault();
  dragCounter = 0;
  dragActive.value = false;
  const files = Array.from(ev.dataTransfer?.files || []);
  if (files.length) await uploadFiles(files);
}

async function uploadFiles(files) {
  for (const file of files) {
    const placeholder = {
      uid: `att-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
      fileId: null,
      fileName: file.name,
      kind: (file.name.split(".").pop() || "").toLowerCase(),
      sizeBytes: file.size,
      uploading: true,
      error: null,
      preview: null,
    };
    attachments.value.push(placeholder);
    const idx = attachments.value.length - 1;
    try {
      const fd = new FormData();
      fd.append("file", file, file.name);
      const res = await fetch("/api/upload", { method: "POST", body: fd });
      if (!res.ok) throw new Error(`upload failed (${res.status})`);
      const data = await res.json();
      const result = data.files?.[0];
      if (!result?.ok) throw new Error(result?.error || "upload rejected");
      // Replace via proxy so Vue picks up the change
      attachments.value[idx] = {
        ...placeholder,
        fileId: result.fileId,
        kind: result.kind,
        sizeBytes: result.sizeBytes,
        preview: result.preview || null,
        uploading: false,
        error: null,
      };
    } catch (e) {
      attachments.value[idx] = {
        ...placeholder,
        uploading: false,
        error: e.message || String(e),
      };
    }
  }
}

function removeAttachment(att) {
  // Drop from the client list immediately, and tell the server to remove it
  // from the per-user listing so the next chat turn no longer surfaces this
  // fileId in the [UPLOADED FILES…] context block. The temp file is kept
  // on disk so prior tool-call results in chat history remain valid; full
  // disposal happens on /api/chat/reset or via the 30-min TTL.
  attachments.value = attachments.value.filter((a) => a !== att);
  if (att.fileId) {
    fetch(`/api/uploads/${encodeURIComponent(att.fileId)}`, {
      method: "DELETE",
    }).catch(() => {});
  }
}

function formatBytes(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

// ── File classifier ────────────────────────────────────────────────────────────
// Inspects filename + preview to label each upload (e.g. "Cost / billing data",
// "Advisor recommendations") so the Analyze prompt can tell the LLM what kind
// of files it's getting. Purely descriptive — no per-purpose prompt menus.
const PURPOSE_LABELS = {
  cost_data: "Cost / billing data",
  resource_inventory: "Resource inventory",
  advisor_recs: "Advisor recommendations",
  cost_summary: "Cost summary / report data",
  audit_log: "Log / audit trail",
  notes_md: "Notes / markdown",
  finops_report_pdf: "FinOps report (PDF)",
  spreadsheet_inventory: "Multi-sheet workbook",
};

function classifyAttachment(att) {
  const name = (att.fileName || "").toLowerCase();
  const kind = att.kind;
  const preview = att.preview || {};
  const cols = (preview.columns || []).map((c) => String(c).toLowerCase());
  const any = (...needles) => needles.some((n) => cols.includes(n));

  if (["csv", "tsv", "parquet"].includes(kind)) {
    if (
      any("pretaxcost", "cost", "unitprice") &&
      any("servicename", "meterid", "meter", "metercategory")
    )
      return PURPOSE_LABELS.cost_data;
    if (
      any("id", "resourceid") &&
      any("type", "sku", "skuname", "resourcetype")
    )
      return PURPOSE_LABELS.resource_inventory;
    if (/cost|spend|billing|usage/.test(name)) return PURPOSE_LABELS.cost_data;
    if (/inventory|resource|asset/.test(name))
      return PURPOSE_LABELS.resource_inventory;
  }
  if (kind === "xlsx") return PURPOSE_LABELS.spreadsheet_inventory;
  if (kind === "json") {
    if (preview.shape === "array") {
      const sample = preview.first_items?.[0] || {};
      const keys = Object.keys(sample).map((k) => k.toLowerCase());
      if (
        keys.includes("category") &&
        (keys.includes("impact") || keys.includes("shortdescription"))
      )
        return PURPOSE_LABELS.advisor_recs;
      if (/advisor|recommend/.test(name)) return PURPOSE_LABELS.advisor_recs;
    } else if (preview.shape === "object") {
      const schemaKeys = Object.keys(preview.schema || {}).map((k) =>
        k.toLowerCase(),
      );
      if (
        schemaKeys.some((k) =>
          /total|byservice|bysubscription|anomal|tagbreakdown|forecast/.test(k),
        )
      )
        return PURPOSE_LABELS.cost_summary;
    }
    if (/cost|export|summary|report/.test(name))
      return PURPOSE_LABELS.cost_summary;
  }
  if (kind === "pdf") return PURPOSE_LABELS.finops_report_pdf;
  if (kind === "txt") {
    if (/\.log$/.test(name) || /audit|access|trace/.test(name))
      return PURPOSE_LABELS.audit_log;
    if (/\.md$/.test(name) || /note|playbook|finding|review/.test(name))
      return PURPOSE_LABELS.notes_md;
  }
  return `${kind.toUpperCase()} file`;
}

const readyAttachments = computed(() =>
  attachments.value.filter((a) => a.fileId && !a.error),
);

const analyzePrompt = computed(() => {
  const list = readyAttachments.value;
  if (!list.length) return "";
  if (list.length === 1) {
    const a = list[0];
    const label = classifyAttachment(a);
    return `Analyze the uploaded file '${a.fileName}' (${label}). The schema is already in your context — go straight to the most useful aggregate/filter calls (no preview round-trip). Produce: (1) one-paragraph summary of what the data is, (2) 3-5 key numbers, (3) the top 3 FinOps insights or anomalies, (4) **call SuggestFollowUp with 3 distinct next actions** (label/prompt, label2/prompt2, label3/prompt3) — e.g. drill into the top finding, generate a remediation script for the top issue, build a CFO summary deck.`;
  }
  const labels = [...new Set(list.map(classifyAttachment))];
  return `Analyze the ${list.length} uploaded files (${labels.join(", ")}). The schema for each is already in your context — pick the right QueryUploadedFile calls (filter / aggregate / json_path / text_range) instead of dumping rows. Produce a one-page assessment: (1) what each file contains in one line, (2) cross-file insights where they relate (e.g. join cost data with inventory, match Advisor recs to actual cost), (3) the top 5 FinOps opportunities ranked by $ impact, (4) **call SuggestFollowUp with 3 distinct next actions** (label/prompt, label2/prompt2, label3/prompt3) — each must reference a concrete entity from the analysis. Suggested mix: deep-dive into the #1 opportunity, generate a remediation script, build a CFO deck.`;
});

function autoGrowInput() {
  const el = inputEl.value;
  if (!el) return;
  el.style.height = "auto";
  el.style.height = Math.min(el.scrollHeight, 400) + "px";
  el.style.overflowY = el.scrollHeight > 400 ? "auto" : "hidden";
}

// Smooth text reveal — drains pending chars fast enough to never lag behind the LLM
let pendingText = "";
let textAnimFrame = null;

function enqueueText(text) {
  pendingText += text;
  if (!textAnimFrame) drainText();
}

function drainText() {
  if (pendingText.length === 0) {
    textAnimFrame = null;
    return;
  }
  // Reveal ~10 chars per frame (60fps = ~600 chars/sec — faster than any LLM)
  const batch = Math.min(10, pendingText.length);
  streamBuffer.value += pendingText.slice(0, batch);
  pendingText = pendingText.slice(batch);
  textAnimFrame = requestAnimationFrame(drainText);
}

function flushText() {
  if (textAnimFrame) {
    cancelAnimationFrame(textAnimFrame);
    textAnimFrame = null;
  }
  if (pendingText.length > 0) {
    streamBuffer.value += pendingText;
    pendingText = "";
  }
}

const hoveredTool = ref(null);
const collapsedSections = reactive({
  subs: true,
  knowledge: true,
  crawl: true,
  walk: true,
  run: true,
  pricing: true,
  playbookRoot: true,
  pb_crawl: true,
  pb_walk: true,
  pb_run: true,
  pb_playbook: true,
  // Maturity cards default to expanded after scoring
  cm_crawl: false,
  cm_walk: false,
  cm_run: false,
  cm_playbook: false,
});
function toggleSection(key) {
  collapsedSections[key] = !collapsedSections[key];
}
const buildSha = ref("");
const buildNumber = ref("0");
const buildBranch = ref("");
const sidebarOpen = ref(
  typeof window !== "undefined" ? window.innerWidth > 768 : true,
);
const plusMenuOpen = ref(false);
const availableModels = ref(["claude-sonnet-4.6"]);
const selectedModel = ref("claude-sonnet-4.6");

// Auth loading state
const authLoading = ref(""); // "" | "github" | "azure"

// Azure connection state
const azureConnected = ref(false);
const azureUserEmail = ref("");
const azureSubscriptions = ref([]);
const azureManagementGroups = ref([]);
const azureApis = ref([]);
const graphEnabled = ref(false);
const licensesEnabled = ref(false);
const chargebackEnabled = ref(false);
const logAnalyticsEnabled = ref(false);
const storageEnabled = ref(false);
const tenantId = ref(""); // User-specified tenant ID or domain for guest users
const availableTenants = ref([]);
const currentTenantId = ref("");
const showTenantSwitcher = ref(false);
const tenantError = ref(false);
const clearing = ref(false);

// ── Knowledge Base state (Entra-only) ────────────────────────────
// Persistent organizational knowledge the agent injects into every
// conversation. List holds metadata only; full content is fetched lazily
// when an article is opened for editing.
const knowledgeArticles = ref([]);
const knowledgeCategories = ref([
  "subscriptions",
  "cost_centers",
  "instructions",
  "architecture",
  "sla",
  "custom",
]);
const knowledgeLimits = reactive({
  maxArticles: 20,
  maxArticleChars: 10000,
  maxTotalChars: 50000,
});
const showKnowledgeModal = ref(false);
const kbSaving = ref(false);
const kbError = ref("");
const kbForm = reactive({ id: "", title: "", category: "custom", content: "" });
const kbFileInput = ref(null);
const kbImporting = ref(false);

const KNOWLEDGE_CATEGORY_LABELS = {
  subscriptions: "Subscription mappings",
  cost_centers: "Cost centers",
  instructions: "Analysis instructions",
  architecture: "Architecture",
  sla: "SLA / RTO / RPO",
  custom: "Custom",
};
function knowledgeCategoryLabel(key) {
  return KNOWLEDGE_CATEGORY_LABELS[key] || key;
}

async function loadKnowledge() {
  try {
    const r = await fetch("/api/knowledge");
    if (!r.ok) {
      // 401 = anonymous session; quietly skip (feature is Entra-only).
      knowledgeArticles.value = [];
      return;
    }
    const data = await r.json();
    knowledgeArticles.value = data.articles || [];
    if (Array.isArray(data.categories) && data.categories.length) {
      knowledgeCategories.value = data.categories;
    }
    if (data.limits) {
      knowledgeLimits.maxArticles = data.limits.maxArticles ?? knowledgeLimits.maxArticles;
      knowledgeLimits.maxArticleChars =
        data.limits.maxArticleChars ?? knowledgeLimits.maxArticleChars;
      knowledgeLimits.maxTotalChars =
        data.limits.maxTotalChars ?? knowledgeLimits.maxTotalChars;
    }
  } catch {
    knowledgeArticles.value = [];
  }
}

function openKnowledgeModal() {
  if (knowledgeArticles.value.length >= knowledgeLimits.maxArticles) return;
  kbForm.id = "";
  kbForm.title = "";
  kbForm.category = "custom";
  kbForm.content = "";
  kbError.value = "";
  showKnowledgeModal.value = true;
}

async function editKnowledgeArticle(a) {
  kbError.value = "";
  kbForm.id = a.id;
  kbForm.title = a.title;
  kbForm.category = a.category;
  kbForm.content = "";
  showKnowledgeModal.value = true;
  try {
    const r = await fetch(`/api/knowledge/${a.id}`);
    if (r.ok) {
      const full = await r.json();
      // Guard against a stale open if the user closed the modal meanwhile.
      if (showKnowledgeModal.value && kbForm.id === full.id) {
        kbForm.content = full.content || "";
      }
    }
  } catch {}
}

function closeKnowledgeModal() {
  showKnowledgeModal.value = false;
}

async function saveKnowledgeArticle() {
  if (kbSaving.value) return;
  kbError.value = "";
  kbSaving.value = true;
  try {
    const body = {
      title: kbForm.title.trim(),
      category: kbForm.category,
      content: kbForm.content,
    };
    const isEdit = !!kbForm.id;
    const r = await fetch(
      isEdit ? `/api/knowledge/${kbForm.id}` : "/api/knowledge",
      {
        method: isEdit ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      },
    );
    if (!r.ok) {
      const err = await r.json().catch(() => ({}));
      kbError.value = err.error || "Could not save. Please try again.";
      return;
    }
    showKnowledgeModal.value = false;
    await loadKnowledge();
  } catch {
    kbError.value = "Network error. Please try again.";
  } finally {
    kbSaving.value = false;
  }
}

async function toggleKnowledgeActive(a) {
  try {
    const r = await fetch(`/api/knowledge/${a.id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ active: !a.active }),
    });
    if (r.ok) await loadKnowledge();
  } catch {}
}

async function deleteKnowledgeArticle(a) {
  if (!confirm(`Delete "${a.title}"? This cannot be undone.`)) return;
  try {
    const r = await fetch(`/api/knowledge/${a.id}`, { method: "DELETE" });
    if (r.ok) await loadKnowledge();
  } catch {}
}

function triggerKnowledgeImport() {
  if (kbImporting.value) return;
  if (knowledgeArticles.value.length >= knowledgeLimits.maxArticles) return;
  kbFileInput.value?.click();
}

async function onKnowledgeFileSelected(e) {
  const file = e.target.files && e.target.files[0];
  // Reset the input so selecting the same file again re-fires change.
  e.target.value = "";
  if (!file) return;
  kbImporting.value = true;
  try {
    const fd = new FormData();
    fd.append("file", file);
    const r = await fetch("/api/knowledge/import", { method: "POST", body: fd });
    if (!r.ok) {
      const err = await r.json().catch(() => ({}));
      alert(err.error || "Could not import file.");
      return;
    }
    await loadKnowledge();
  } catch {
    alert("Network error during import.");
  } finally {
    kbImporting.value = false;
  }
}


// ── Multi-session state (Entra-only) ─────────────────────────────
// `sessions` mirrors the server's view of the user's saved Copilot sessions.
// `currentSessionId` is the one the next /api/chat request will hit. The
// backend echoes it back as the first SSE event of every chat so we always
// stay in sync even if the user clicked a row mid-stream.
const sessions = ref([]);
const currentSessionId = ref(null);

async function loadSessions() {
  if (!azureConnected.value) {
    sessions.value = [];
    currentSessionId.value = null;
    return;
  }
  try {
    const res = await fetch("/api/sessions", { credentials: "same-origin" });
    if (!res.ok) return;
    const data = await res.json();
    sessions.value = Array.isArray(data.sessions) ? data.sessions : [];
    // Only adopt the server's notion of "current" when the client has none.
    // Otherwise a periodic refresh would yank the user out of the session
    // they explicitly selected.
    if (data.currentSessionId && currentSessionId.value == null) {
      currentSessionId.value = data.currentSessionId;
    }
  } catch {
    /* network blip — keep prior list */
  }
}

async function newSession() {
  if (clearing.value) return;
  // Note: we do NOT abort an in-flight stream here. If another session is
  // running in the background, let it keep running — the SSE handlers will
  // detect that the user has navigated away and stop writing to the UI.
  clearing.value = true;
  messages.value = [];
  streamBuffer.value = "";
  // User-initiated wipe — drop every session's live buckets.
  perSessionToolCalls.clear();
  perSessionCharts.clear();
  scriptReady.value = null;
  htmlReady.value = null;
  attachments.value = [];
  activeTools.value = [];
  hoveredTool.value = null;
  input.value = "";
  chartInstances.forEach((c) => {
    try {
      c.dispose();
    } catch {}
  });
  chartInstances.length = 0;
  maturityScores.crawl = null;
  maturityScores.walk = null;
  maturityScores.run = null;
  maturityScores.playbook = null;
  try {
    const res = await fetch("/api/sessions/new", { method: "POST" });
    if (res.ok) {
      const j = await res.json();
      currentSessionId.value = j.sessionId || null;
    }
  } catch {}
  await loadSessions();
  clearing.value = false;
}

async function selectSession(sessionId) {
  if (!sessionId || sessionId === currentSessionId.value) return;
  // Allow switching even if another session is streaming in the background;
  // the in-flight SSE handlers will detect the navigation and skip UI writes.
  try {
    await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/select`, {
      method: "POST",
    });
  } catch {
    return;
  }
  currentSessionId.value = sessionId;
  // Fetch the persisted transcript from the SDK and replay it so the user
  // sees their actual past messages and tool calls — not a placeholder.
  // Wipe transient view-scoped UI refs (intent ticker, partial text buffer,
  // follow-up CTA) so nothing from the previous view leaks. Do NOT touch
  // perSessionToolCalls / perSessionCharts — those are keyed by sessionId
  // and a still-streaming background session needs its bucket intact so the
  // tool list is whole when the user switches back.
  streamFollowUp.value = null;
  streamBuffer.value = "";
  streamIntent.value = "";
  if (intentAnimTimer) {
    clearInterval(intentAnimTimer);
    intentAnimTimer = null;
  }
  activeTools.value = [];
  hoveredTool.value = null;
  scriptReady.value = null;
  htmlReady.value = null;
  try {
    const res = await fetch(
      `/api/sessions/${encodeURIComponent(sessionId)}/messages`,
    );
    if (res.ok) {
      const j = await res.json();
      const restored = (j.messages || []).map((m) => ({
        role: m.role,
        content: m.content || "",
        toolCalls: (m.toolCalls || []).map((tc) => ({
          id: tc.id,
          tool: tc.name,
          args: tc.args || "",
          result: tc.result || null,
          error: tc.error || null,
          success: tc.success !== false,
          intent: tc.intent || "",
          durationMs: null,
          done: true,
          expanded: false,
        })),
        charts: m.charts || [],
        html: m.html || null,
        script: m.script ? { ...m.script, expanded: false } : null,
      }));
      console.log(
        "[loadMessages] /messages returned",
        restored.length,
        "messages. roles=",
        restored.map((m) => m.role).join(","),
        "assistant lengths=",
        restored
          .filter((m) => m.role === "assistant")
          .map((m) => m.content.length),
      );
      // If this session is still streaming in the background, drop any
      // trailing assistant message — it is the in-progress turn that the
      // SDK has already persisted. The live SSE stream will commit the
      // final version on completion. Otherwise the streaming indicator
      // (which renders its own AI avatar) would appear directly below the
      // partial assistant row, producing two stacked AI avatars.
      if (
        runningSessions.has(sessionId) &&
        restored.length &&
        restored[restored.length - 1].role === "assistant"
      ) {
        restored.pop();
      }
      messages.value = restored.length
        ? restored
        : [
            {
              role: "system",
              content: "Resumed conversation. Ask anything to continue.",
            },
          ];

      // Replay any ReportMaturityScore tool calls so the sidebar stars
      // reflect the conversation's last-known scores. Latest call per level wins.
      maturityScores.crawl = null;
      maturityScores.walk = null;
      maturityScores.run = null;
      maturityScores.playbook = null;
      for (const m of restored) {
        for (const tc of m.toolCalls || []) {
          if (tc.tool !== "ReportMaturityScore") continue;
          let level = null;
          let scoresJson = null;
          // Prefer the SSE marker baked into the tool result.
          const marker =
            typeof tc.result === "string" &&
            tc.result.startsWith("__MATURITY_SCORE__:")
              ? tc.result
              : null;
          if (marker) {
            const rest = marker.slice("__MATURITY_SCORE__:".length);
            const idx = rest.indexOf(":");
            if (idx > 0) {
              level = rest.slice(0, idx).toLowerCase();
              scoresJson = rest.slice(idx + 1);
            }
          } else if (tc.args) {
            // Fallback: parse the tool args (level + scores).
            try {
              const a =
                typeof tc.args === "string" ? JSON.parse(tc.args) : tc.args;
              level = (a.level || "").toLowerCase();
              scoresJson = a.scores;
            } catch {}
          }
          if (
            level &&
            ["crawl", "walk", "run", "playbook"].includes(level) &&
            scoresJson
          ) {
            try {
              const arr =
                typeof scoresJson === "string"
                  ? JSON.parse(scoresJson)
                  : scoresJson;
              if (Array.isArray(arr)) maturityScores[level] = arr;
            } catch {}
          }
        }
      }
    } else {
      const meta = sessions.value.find((s) => s.id === sessionId);
      messages.value = [
        {
          role: "system",
          content: `Resumed conversation: ${meta?.summary || "Untitled"}.`,
        },
      ];
    }
  } catch {
    messages.value = [{ role: "system", content: "Resumed conversation." }];
  }
}

async function deleteSession(sessionId) {
  try {
    await fetch(`/api/sessions/${encodeURIComponent(sessionId)}`, {
      method: "DELETE",
    });
  } catch {
    return;
  }
  if (sessionId === currentSessionId.value) {
    currentSessionId.value = null;
    messages.value = [];
    streamFollowUp.value = null;
    scriptReady.value = null;
    htmlReady.value = null;
    maturityScores.crawl = null;
    maturityScores.walk = null;
    maturityScores.run = null;
    maturityScores.playbook = null;
  }
  // Drop the live-stream buckets for the deleted session regardless of view —
  // a backgrounded stream may still be writing to it; abandon those writes.
  perSessionToolCalls.delete(sessionId);
  perSessionCharts.delete(sessionId);
  await loadSessions();
}

function formatRelativeTime(iso) {
  if (!iso) return "";
  const t = new Date(iso).getTime();
  if (!t) return "";
  const diff = Date.now() - t;
  const min = Math.round(diff / 60000);
  if (min < 1) return "just now";
  if (min < 60) return `${min}m ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const d = Math.round(hr / 24);
  if (d < 30) return `${d}d ago`;
  const mo = Math.round(d / 30);
  return `${mo}mo ago`;
}

// Add-ons collapsible parent + per-row open state + onboarding glow tour
// Defaults to collapsed; the first-login tour expands → animates each row → collapses again.
const addonsOpen = ref(false);
const addonRowsOpen = ref([false, false, false, false]);
const glowingRow = ref(-1);

// One-time onboarding tour: when user first connects, briefly open each
// add-on row (with a glow) so they discover the click-to-expand UI, then
// collapse the whole parent so only the email + "Add scopes" toggle remain.
// Runs once per browser (localStorage). To force-replay: localStorage.removeItem('addons-tour-shown')
async function runAddonsTour() {
  if (localStorage.getItem("addons-tour-shown")) return;
  await new Promise((r) => setTimeout(r, 900));
  addonsOpen.value = true;
  for (let i = 0; i < 4; i++) {
    glowingRow.value = i;
    addonRowsOpen.value[i] = true;
    await new Promise((r) => setTimeout(r, 1400));
    addonRowsOpen.value[i] = false;
    glowingRow.value = -1;
    await new Promise((r) => setTimeout(r, 300));
  }
  // Final beat — let the user see the full collapsed list, then minimise
  // the parent so the sidebar shows just the email + "Add scopes" toggle.
  await new Promise((r) => setTimeout(r, 700));
  addonsOpen.value = false;
  // Mark as shown only after the tour completes successfully so partial
  // runs (e.g. user navigates away mid-tour) replay on next visit.
  localStorage.setItem("addons-tour-shown", "1");
}

// Click on a scope row: just toggle the details panel.
// Adding the scope is done via the explicit "Add scope" button inside.
function clickScopeRow(idx) {
  addonRowsOpen.value[idx] = !addonRowsOpen.value[idx];
}

// Resolve a tenant GUID to a friendly display name from availableTenants.
function tenantNameFor(tenantId) {
  if (!tenantId) return "";
  const t = availableTenants.value.find((x) => x.tenantId === tenantId);
  return t ? t.displayName || t.defaultDomain || "" : "";
}

async function checkAzureStatus() {
  try {
    const r = await fetch("/auth/azure/status");
    if (r.ok) {
      const data = await r.json();
      azureConnected.value = data.connected;
      if (data.connected) {
        azureUserEmail.value = data.user?.email || data.user?.name || "";
        azureSubscriptions.value = data.subscriptions || [];
        azureManagementGroups.value = data.managementGroups || [];
        azureApis.value = data.apis || [];
        graphEnabled.value = data.graphEnabled || false;
        const gt = data.graphTier || "";
        licensesEnabled.value = gt.includes("licenses");
        chargebackEnabled.value = gt.includes("chargeback");
        logAnalyticsEnabled.value = data.logAnalyticsEnabled || false;
        storageEnabled.value = data.storageEnabled || false;
        // Fetch tenant list after connecting
        fetchTenants();
        // Load the user's persistent knowledge base (Entra-only)
        loadKnowledge();
        // First-time onboarding: glow-tour the add-on rows
        runAddonsTour();
      }
    }
  } catch {}
}

async function fetchTenants() {
  try {
    const r = await fetch("/auth/azure/tenants");
    if (r.ok) {
      const data = await r.json();
      availableTenants.value = data.tenants || [];
      currentTenantId.value = data.currentTenantId || "";
      // Persist tenants to localStorage so they appear as clickable buttons
      // on future visits — even before connecting
      if (availableTenants.value.length > 0) {
        localStorage.setItem(
          "knownTenants",
          JSON.stringify(availableTenants.value),
        );
      }
    }
  } catch {}
}

// Load previously discovered tenants from localStorage
const savedTenants = computed(() => {
  try {
    return JSON.parse(localStorage.getItem("knownTenants") || "[]");
  } catch {
    return [];
  }
});

function switchTenant(tid) {
  startAuth("azure", "/auth/microsoft?tenant=" + encodeURIComponent(tid));
}

function startAuth(provider, url) {
  authLoading.value = provider;
  sessionStorage.setItem("authLoading", provider);
  // Append tenant param if user specified one (for guest users with multiple tenants)
  let authUrl = url;
  if (tenantId.value.trim()) {
    const sep = url.includes("?") ? "&" : "?";
    authUrl += sep + "tenant=" + encodeURIComponent(tenantId.value.trim());
  }
  setTimeout(() => {
    window.location.href = authUrl;
  }, 100);
}

async function disconnectAzure() {
  try {
    await fetch("/auth/azure/disconnect", { method: "POST" });
    azureConnected.value = false;
    azureUserEmail.value = "";
    azureSubscriptions.value = [];
    knowledgeArticles.value = [];
    azureManagementGroups.value = [];
    azureApis.value = [];
    graphEnabled.value = false;
    licensesEnabled.value = false;
    chargebackEnabled.value = false;
    logAnalyticsEnabled.value = false;
    storageEnabled.value = false;
    maturityScores.crawl = null;
    maturityScores.walk = null;
    maturityScores.run = null;
    maturityScores.playbook = null;
    // Reset onboarding so a fresh reconnect replays the add-ons tour.
    localStorage.removeItem("addons-tour-shown");
    await clearMessages();
  } catch {}
}

async function revokeAllPermissions() {
  if (
    !confirm(
      "This will disconnect your session and clear all tokens. When you reconnect, you'll see the consent screen again.\n\nTo fully revoke app permissions in Entra ID, visit: https://myapps.microsoft.com\n\nContinue?",
    )
  )
    return;
  try {
    // Server-side revoke removes consent grants from Entra ID
    await fetch("/auth/azure/revoke", { method: "POST" });
    azureConnected.value = false;
    azureUserEmail.value = "";
    azureSubscriptions.value = [];
    knowledgeArticles.value = [];
    azureManagementGroups.value = [];
    azureApis.value = [];
    graphEnabled.value = false;
    licensesEnabled.value = false;
    chargebackEnabled.value = false;
    logAnalyticsEnabled.value = false;
    storageEnabled.value = false;
    // Reset onboarding so the next reconnect replays the add-ons tour.
    localStorage.removeItem("addons-tour-shown");
    await clearMessages();
  } catch {}
}

// When Azure connects, force Crawl/Walk/Run/Playbook/Pricing to collapsed.
// This guarantees a clean initial state every time the user reconnects.
watch(azureConnected, async (connected, wasConnected) => {
  if (!connected) {
    sessions.value = [];
    currentSessionId.value = null;
    return;
  }
  collapsedSections.pricing = true;
  collapsedSections.crawl = true;
  collapsedSections.walk = true;
  collapsedSections.run = true;
  collapsedSections.playbook = true;
  collapsedSections.playbookRoot = true;
  collapsedSections.pb_crawl = true;
  collapsedSections.pb_walk = true;
  collapsedSections.pb_run = true;
  collapsedSections.pb_playbook = true;
  // Auto-clear chat when Azure connects — removes stale "Connect Azure first" messages
  // and resets the Copilot session so the LLM knows the user is now connected
  if (!wasConnected) await clearMessages();
  // Hydrate the Conversations sidebar with this user's saved sessions.
  await loadSessions();
});

// Reset Copilot session when addon tiers are enabled so LLM picks up new tokens
watch(graphEnabled, async (enabled, was) => {
  if (enabled && !was) {
    await clearMessages();
  }
});
watch(logAnalyticsEnabled, async (enabled, was) => {
  if (enabled && !was) {
    await clearMessages();
  }
});
watch(storageEnabled, async (enabled, was) => {
  if (enabled && !was) {
    await clearMessages();
  }
});

function dismissPopover() {
  hoveredTool.value = null;
}

async function fetchModels() {
  try {
    const r = await fetch("/api/models");
    if (r.ok) {
      const models = await r.json();
      if (Array.isArray(models) && models.length > 0) {
        const ids = models.map((m) => m.id || m.name || m).filter(Boolean);
        availableModels.value = ids;
        const opusModels = ids.filter((id) => /opus/i.test(id));
        if (opusModels.length > 0) {
          opusModels.sort();
          selectedModel.value = opusModels[opusModels.length - 1];
        }
      }
    }
  } catch {}
}

// Handle user login/logout
watch(
  () => props.user,
  (newUser, oldUser) => {
    if (newUser && !oldUser) {
      // User just logged in — fetch models and Azure status
      fetchModels();
      checkAzureStatus();
    } else if (!newUser && oldUser) {
      // User logged out — clear all UI state
      clearMessages();
      azureConnected.value = false;
      azureUserEmail.value = "";
      azureSubscriptions.value = [];
      knowledgeArticles.value = [];
      azureManagementGroups.value = [];
      azureApis.value = [];
      input.value = "";
      if (abortController) {
        abortController.abort();
        abortController = null;
      }
    }
  },
);

onMounted(async () => {
  document.addEventListener("click", dismissPopover);
  // Dev helper: window.__simulateCool(waitSec?) injects a synthetic ghost
  // "cooling down" row in the current session's tool sidebar so you can
  // verify the throttle UI without forcing a real 429. Removes itself
  // after the wait elapses, just like a real cooler. Dev-build only —
  // gated on Vite's import.meta.env.DEV so production bundles don't ship it.
  if (import.meta.env.DEV) {
    window.__simulateCool = (wait = 15, status = 429) => {
      const sid = currentSessionId.value || "__pending__";
      const list = perSessionCoolers.get(sid) || [];
      const cooler = {
        _uid: `c-sim-${Date.now()}`,
        _key: `sim|${Date.now()}`,
        _isCooler: true,
        tool: "azure",
        url: "https://management.azure.com/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.CostManagement/query?api-version=2025-03-01",
        attempt: 1,
        wait,
        status,
        ts: Date.now(),
        expanded: false,
        done: false,
      };
      cooler._timer = setTimeout(() => {
        const arr = perSessionCoolers.get(sid) || [];
        perSessionCoolers.set(sid, arr.filter((x) => x._uid !== cooler._uid));
      }, wait * 1000 + 500);
      list.push(cooler);
      perSessionCoolers.set(sid, [...list]);
      // eslint-disable-next-line no-console
      console.log("[simulateCool] injected ghost row for", wait, "s in session", sid);
      return cooler._uid;
    };
  }
  // Load saved conversations once we know whether the user is Azure-connected.
  // (The auth check fires on mount too; loadSessions is a no-op until then.)
  setTimeout(() => {
    loadSessions();
  }, 500);
  // Restore auth loading state after OAuth redirect (page reload clears ref)
  const pendingAuth = sessionStorage.getItem("authLoading");
  if (pendingAuth) {
    authLoading.value = pendingAuth;
  }
  // Handle Azure OAuth error redirect
  const params = new URLSearchParams(window.location.search);
  const azureError = params.get("azure_error");
  if (azureError) {
    // Show tenant picker with error banner — the user's home tenant likely blocked the app
    tenantError.value = true;
    window.history.replaceState({}, "", window.location.pathname);
  }
  // Handle admin consent success — chain through each tier silently to populate
  // session tokens. Each redirect is silent because admin pre-approved everything.
  const adminConsentOk = params.get("admin_consent");
  if (adminConsentOk === "ok") {
    window.history.replaceState({}, "", window.location.pathname);
    sessionStorage.setItem("authLoading", "azure");
    authLoading.value = "azure";
    // Kick off silent token acquisition for the first add-on tier — the
    // existing tier-based flow handles the rest as the user clicks each.
    // Fastest UX: redirect to licenses tier first; user will see brief loading then refresh
    setTimeout(() => {
      window.location.href = "/auth/microsoft?tier=licenses&postadmin=1";
    }, 400);
    return;
  }
  try {
    const r = await fetch("/api/version");
    if (r.ok) {
      const v = await r.json();
      buildSha.value = v.sha || "";
      buildNumber.value = v.build || "0";
      buildBranch.value = v.branch || "";
    }
  } catch {}
  // Fetch available models and Azure status in parallel
  if (props.user) {
    await Promise.all([fetchModels(), checkAzureStatus()]);
  }
  // Clear auth loading overlay now that status is resolved
  if (pendingAuth) {
    sessionStorage.removeItem("authLoading");
    authLoading.value = "";
  }
});

let abortController = null;

async function clearMessages() {
  // Prevent concurrent send() while resetting
  clearing.value = true;
  // Abort any in-flight request first
  if (abortController) {
    abortController.abort();
    abortController = null;
  }
  messages.value = [];
  streamBuffer.value = "";
  perSessionToolCalls.clear();
  perSessionCharts.clear();
  streamFollowUp.value = null;
  streamIntent.value = "";
  if (intentAnimTimer) {
    clearInterval(intentAnimTimer);
    intentAnimTimer = null;
  }
  scriptReady.value = null;
  htmlReady.value = null;
  attachments.value = [];
  activeTools.value = [];
  hoveredTool.value = null;
  // Drop any tracked background streams — on logout/reset they belong to
  // a different identity and would otherwise leave stale "live" dots.
  runningSessions.clear();
  input.value = "";
  chartInstances.forEach((c) => {
    try {
      c.dispose();
    } catch {}
  });
  chartInstances.length = 0;
  // Reset FinOps maturity scores in the sidebar
  maturityScores.crawl = null;
  maturityScores.walk = null;
  maturityScores.run = null;
  maturityScores.playbook = null;
  try {
    const r = await fetch("/api/chat/reset", { method: "POST" });
    if (r.ok) {
      try {
        const j = await r.json();
        if (j && j.sessionId) currentSessionId.value = j.sessionId;
      } catch {}
    }
  } catch {}
  await loadSessions();
  clearing.value = false;
}

function stopGeneration() {
  if (abortController) {
    abortController.abort();
    abortController = null;
  }
}

const reversedToolCalls = computed(() => [...allToolCalls.value].reverse());

// Live status line under the "Agent" header — shows what the agent is doing right now.
const agentStatus = computed(() => {
  if (!streaming.value) {
    return allToolCalls.value.length > 0 ? "idle" : "ready";
  }
  // Find the most recent in-flight tool call
  const running = [...allToolCalls.value].reverse().find((t) => !t.done);
  if (running) return friendlyToolLabel(running) + "…";
  return "thinking…";
});

function formatDuration(ms) {
  if (ms == null) return "";
  if (ms < 1000) return ms + "ms";
  return (ms / 1000).toFixed(1) + "s";
}

// ── Friendly tool labels ──
// Detect which Azure API a generic QueryAzure / BulkAzureRequest call is hitting
// based on the URL path, and return a short user-facing label that highlights
// the breadth of APIs the agent uses (Cost Management, Resource Graph, etc.).
const AZURE_API_LABELS = [
  // Order matters: more specific first.
  [/microsoft\.costmanagement.*\/forecast/i, "Forecast API"],
  [/microsoft\.costmanagement.*\/exports/i, "Cost Exports"],
  [/microsoft\.costmanagement.*\/scheduledactions/i, "Scheduled Actions"],
  [/microsoft\.costmanagement.*\/views/i, "Cost Views"],
  [/microsoft\.costmanagement/i, "Cost Management"],
  [/microsoft\.consumption.*\/budgets/i, "Budgets"],
  [/microsoft\.consumption.*\/reservation/i, "Reservations"],
  [/microsoft\.consumption.*\/pricesheet/i, "Pricesheet"],
  [/microsoft\.consumption/i, "Consumption"],
  [/microsoft\.billing/i, "Billing"],
  [/microsoft\.capacity\/reservation/i, "Reservations"],
  [/microsoft\.billingbenefits/i, "Savings Plans"],
  [/microsoft\.advisor/i, "Advisor"],
  [/microsoft\.resourcegraph/i, "Resource Graph"],
  [/providers\/microsoft\.insights\/metrics/i, "Azure Monitor Metrics"],
  [/providers\/microsoft\.insights\/eventtypes/i, "Activity Log"],
  [/microsoft\.insights/i, "Azure Monitor"],
  [/microsoft\.operationalinsights/i, "Log Analytics"],
  [/microsoft\.compute\/virtualmachines/i, "Compute · VMs"],
  [/microsoft\.compute\/disks/i, "Compute · Disks"],
  [/microsoft\.compute/i, "Compute"],
  [/microsoft\.containerservice/i, "AKS"],
  [/microsoft\.app\/containerapps/i, "Container Apps"],
  [/microsoft\.network\/publicipaddresses/i, "Network · Public IPs"],
  [/microsoft\.network\/virtualnetworks/i, "Network · VNets"],
  [/microsoft\.network/i, "Network"],
  [/microsoft\.storage/i, "Storage"],
  [/microsoft\.sql/i, "Azure SQL"],
  [/microsoft\.web/i, "App Service"],
  [/microsoft\.documentdb/i, "Cosmos DB"],
  [/microsoft\.cache\/redis/i, "Redis Cache"],
  [/microsoft\.machinelearningservices/i, "Azure ML"],
  [/microsoft\.cognitiveservices/i, "AI Foundry"],
  [/microsoft\.databricks/i, "Databricks"],
  [/microsoft\.datafactory/i, "Data Factory"],
  [/microsoft\.synapse/i, "Synapse"],
  [/microsoft\.security\/securescores/i, "Defender · Secure Score"],
  [/microsoft\.security/i, "Defender for Cloud"],
  [/microsoft\.authorization\/roleassignments/i, "RBAC"],
  [/microsoft\.authorization\/policy/i, "Policy"],
  [/microsoft\.authorization\/locks/i, "Resource Locks"],
  [/microsoft\.authorization/i, "Authorization"],
  [/microsoft\.policyinsights/i, "Policy Insights"],
  [/microsoft\.management\/managementgroups/i, "Management Groups"],
  [/microsoft\.resourcehealth/i, "Resource Health"],
  [/microsoft\.quota/i, "Quota"],
  [/microsoft\.carbon/i, "Carbon"],
  [/microsoft\.migrate/i, "Migrate"],
  [/microsoft\.support/i, "Support"],
  [/\/subscriptions(\?|\/?$)/i, "Subscriptions"],
  [/\/tenants/i, "Tenants"],
  [/\/resources(\?|\/?$)/i, "Resource Manager"],
  [/\/providers/i, "Providers"],
  [/\/resourcegroups/i, "Resource Groups"],
];

function _arm_label(path) {
  if (!path) return null;
  for (const [re, label] of AZURE_API_LABELS) {
    if (!re.test(path)) continue;
    // Several distinct REST operations can share the same friendly label
    // (e.g. /query, /dimensions, /alerts all map to "Cost Management").
    // Append the last action/segment so parallel calls don't look like duplicates.
    const tail = (path.split("?")[0].match(/\/([A-Za-z0-9_-]+)\/?$/) || [])[1];
    if (tail && !new RegExp(tail, "i").test(label)) {
      const t = tail.toLowerCase();
      // Skip noisy id-like tails (guids, version segments).
      if (!/^[0-9a-f]{8,}$/i.test(tail) && !/^\d{4}-\d{2}-\d{2}/.test(tail))
        return `${label} · ${t}`;
    }
    return label;
  }
  return "ARM";
}

function _graph_label(path) {
  if (!path) return null;
  if (/subscribedSkus/i.test(path)) return "Graph · Licenses";
  if (/getMicrosoft365Copilot/i.test(path)) return "Graph · M365 Copilot";
  if (/getM365App/i.test(path)) return "Graph · M365 Apps";
  if (
    /getOffice365|getMailbox|getTeams|getOneDrive|getSharePoint|getEmail/i.test(
      path,
    )
  )
    return "Graph · M365 Reports";
  if (/managedDevices|deviceCompliance/i.test(path)) return "Graph · Intune";
  if (/secureScore/i.test(path)) return "Graph · Secure Score";
  if (/\/users/i.test(path)) return "Graph · Users";
  if (/\/groups/i.test(path)) return "Graph · Groups";
  if (/\/organization/i.test(path)) return "Graph · Org";
  if (/directoryRoles|roleManagement/i.test(path)) return "Graph · Roles";
  return "Microsoft Graph";
}

function friendlyToolLabel(tc) {
  if (!tc) return "";
  // Live 429 backoff — set by cooling_down SSE event mid-flight.
  if (tc.cooling && !tc.done) {
    const w = Math.round(tc.cooling.wait || 0);
    return `Cooling down… ${w}s`;
  }
  // Throttled HTTP calls: the tool itself succeeded (returned a string), but the
  // body starts with "HTTP 429 …". Show a friendly status instead of the tool name
  // so the user understands it was rate-limited, not a hard failure.
  if (tc.done && typeof tc.result === "string" && tc.result.startsWith("HTTP 429")) {
    return "Cooling down…";
  }
  const tool = tc.tool;
  let args = tc.args;
  if (args && typeof args === "string") {
    try {
      args = JSON.parse(args);
    } catch {
      args = null;
    }
  }
  if (tool === "QueryAzure") {
    const path = args?.path || args?.url || "";
    return _arm_label(path) || "Azure";
  }
  if (tool === "BulkAzureRequest") {
    const items = args?.requests || args?.items || [];
    const n = Array.isArray(items) ? items.length : 0;
    return n ? `Bulk ×${n}` : "Bulk";
  }
  if (tool === "QueryGraph") {
    return _graph_label(args?.path || args?.url || "") || "Graph";
  }
  if (tool === "QueryLogAnalytics") return "KQL";
  if (tool === "GetAzureRetailPricing") {
    const sku = args?.armSkuName || args?.skuName || args?.serviceName || "";
    const head = String(sku).split(/[\s_]/)[0];
    return head ? `Pricing · ${head}` : "Pricing";
  }
  if (tool === "GetAzureServiceHealth") return "Health";
  if (tool === "GenerateHtmlPresentation") {
    let n = 0;
    try {
      const slides = args?.slidesJson ? JSON.parse(args.slidesJson) : null;
      n = Array.isArray(slides) ? slides.length : 0;
    } catch {}
    return n ? `Deck ×${n}` : "Deck";
  }
  if (tool === "GenerateScript") {
    const lang = (args?.language || args?.lang || "").toLowerCase();
    if (lang.includes("powershell") || lang.includes("ps")) return "PowerShell";
    if (lang.includes("bash") || lang.includes("sh")) return "Bash";
    return "Script";
  }
  if (tool === "RenderChart" || tool === "RenderAdvancedChart") {
    const t = (args?.type || args?.chartType || "").toLowerCase();
    if (t.includes("bar")) return "Chart · bar";
    if (t.includes("pie") || t.includes("doughnut")) return "Chart · pie";
    if (t.includes("line")) return "Chart · line";
    if (t.includes("map")) return "Chart · map";
    return "Chart";
  }
  if (tool === "ReportMaturityScore") {
    const lvl = (args?.level || "").toLowerCase();
    return lvl ? `Score · ${lvl}` : "Score";
  }
  if (tool === "GetScoreHistory") return "History";
  if (tool === "DetectCostAnomalies") return "Anomalies";
  if (tool === "FindIdleResources") return "Idle";
  if (tool === "ListCostExportBlobs") return "Exports";
  if (tool === "ReadCostExportBlob") return "Export";
  if (tool === "SaveReportSchedule") return "Schedule +";
  if (tool === "ListReportSchedules") return "Schedules";
  if (tool === "DeleteReportSchedule") return "Schedule −";
  if (tool === "PublishFAQ") return "FAQ";
  if (tool === "SuggestFollowUp") return "Follow-up";
  if (tool === "QueryUploadedFile") {
    const mode = (args?.mode || "").toLowerCase();
    return mode ? `File · ${mode}` : "File";
  }
  if (tool === "report_intent") return "Intent";
  return tool;
}

// ── Prompt categories ──
// New simplified layout for the demo:
//   - Crawl / Walk / Run rendered as 3 hero "Score" buttons (with stars when scored)
//   - All detailed prompts collapsed under a single "Playbook" parent
//   - Pricing & Estimates always visible (no login required)
const SCORE_KEYS = ["crawl", "walk", "run"];

// Hero score categories: just label + subtitle + score CTA + (post-score) results
const scoreCategories = computed(() =>
  SCORE_KEYS.map((key) => {
    const cat = maturityCategories.find((c) => c.key === key);
    if (!cat) return null;
    const scorePrompt =
      (cat.prompts.find((p) => p.label.startsWith("Score ")) || {}).prompt ||
      "";
    return {
      key: cat.key,
      label: cat.label,
      subtitle: cat.subtitle,
      scorePrompt,
    };
  }).filter(Boolean),
);

// Playbook groups: every maturity level's non-Score prompts. Pricing has its
// own dedicated sidebar card and is no longer in maturityCategories.
const playbookGroups = computed(() =>
  maturityCategories.map((cat) => ({
    key: cat.key,
    label: cat.label,
    prompts: cat.prompts.filter((p) => !p.label.startsWith("Score ")),
  })),
);

// Pricing prompts: when user is connected, append the personalised prompts
// that cross-reference real Azure resources with retail pricing.
const pricingPromptsForUser = computed(() =>
  azureConnected.value
    ? [
        ...(pricingCategory.connectedPrompts || []),
        ...(pricingCategory.publicPrompts || pricingCategory.prompts),
      ]
    : pricingCategory.publicPrompts || pricingCategory.prompts,
);

// ── Maturity scores (set by LLM via ReportMaturityScore tool → SSE) ──
const maturityScores = reactive({
  crawl: null, // null = not scored, array of {id, label, score, detail} when scored
  walk: null,
  run: null,
  playbook: null,
});

function maturityOverall(level) {
  const scores = maturityScores[level];
  if (!scores || scores.length === 0) return -1;
  return Math.round(
    scores.reduce((sum, s) => sum + s.score, 0) / scores.length,
  );
}

function maturityNumeric(level) {
  const scores = maturityScores[level];
  if (!scores || scores.length === 0) return "0.0";
  const avg = scores.reduce((sum, s) => sum + s.score, 0) / scores.length;
  return avg.toFixed(1);
}

function starsText(score) {
  if (score < 0) return "☆☆☆☆☆";
  const full = Math.min(score, 5);
  return "★".repeat(full) + "☆".repeat(5 - full);
}

function starColor(score) {
  if (score >= 4) return "#107c10";
  if (score >= 3) return "#ff8c00";
  if (score >= 1) return "#d83b01";
  return "#a19f9d";
}

// The HTML download card is shared by the slide-deck renderer and the maturity
// report renderer (both emit the same __HTML_READY__ marker). Decks put a bare
// number in the count slot (e.g. "7"); the report puts a labelled string (e.g.
// "3 capabilities"). Detect which and word the card accordingly.
function htmlCardIsReport(count) {
  return /[a-zA-Z]/.test(String(count ?? ""));
}
function htmlCardTitle(count) {
  return htmlCardIsReport(count) ? "FinOps report ready" : "FinOps presentation ready";
}
function htmlCardMeta(count) {
  const s = String(count ?? "");
  return htmlCardIsReport(count) ? s : `${s} slides`;
}

// Sidebar categories (FinOps maturity Crawl/Walk/Run/Playbook + Pricing) — see data/sidebarCategories.js

function sendQuestion(q) {
  if (streaming.value || clearing.value || !props.user) return;
  input.value = q;
  send();
}

// ── ECharts rendering ──

// Country name aliases → Natural Earth canonical names used by world-atlas GeoJSON.
// The LLM may produce short/common names; this map normalizes them so ECharts can match features.
const COUNTRY_NAME_ALIASES = {
  "United States": "United States of America",
  US: "United States of America",
  USA: "United States of America",
  "U.S.": "United States of America",
  "U.S.A.": "United States of America",
  Russia: "Russia",
  "South Korea": "South Korea",
  Korea: "South Korea",
  "Republic of Korea": "South Korea",
  "North Korea": "North Korea",
  "Dem. Rep. Korea": "North Korea",
  DPRK: "North Korea",
  "Czech Republic": "Czechia",
  "DR Congo": "Dem. Rep. Congo",
  "Democratic Republic of the Congo": "Dem. Rep. Congo",
  "Congo (DRC)": "Dem. Rep. Congo",
  "Republic of the Congo": "Congo",
  Tanzania: "United Republic of Tanzania",
  "United Republic of Tanzania": "United Republic of Tanzania",
  "Ivory Coast": "Côte d'Ivoire",
  "Cote d'Ivoire": "Côte d'Ivoire",
  Bosnia: "Bosnia and Herzegovina",
  "Bosnia & Herzegovina": "Bosnia and Herzegovina",
  UAE: "United Arab Emirates",
  UK: "United Kingdom",
  Britain: "United Kingdom",
  "Great Britain": "United Kingdom",
  "Dominican Rep.": "Dominican Republic",
  "Central African Rep.": "Central African Republic",
  "Eq. Guinea": "Equatorial Guinea",
  eSwatini: "eSwatini",
  Swaziland: "eSwatini",
  "East Timor": "Timor-Leste",
  Burma: "Myanmar",
  Laos: "Lao PDR",
  Vatican: "Vatican City",
  Palestine: "Palestine",
  "Falkland Islands": "Falkland Islands",
  Macedonia: "North Macedonia",
  FYROM: "North Macedonia",
};

function normalizeCountryName(name) {
  return COUNTRY_NAME_ALIASES[name] || name;
}

// Normalize all data items in map-type series
function normalizeMapSeriesData(opts) {
  if (!opts || !opts.series) return opts;
  const series = Array.isArray(opts.series) ? opts.series : [opts.series];
  for (const s of series) {
    if (s.type === "map" && Array.isArray(s.data)) {
      s.data = s.data.map((d) => ({
        ...d,
        name: normalizeCountryName(d.name),
      }));
    }
  }
  return opts;
}

// World map GeoJSON cache
let worldMapLoaded = false;
let worldMapLoading = null;

function replaceWithChartFallback(option, message) {
  for (const key of Object.keys(option)) {
    delete option[key];
  }

  Object.assign(option, {
    backgroundColor: "transparent",
    title: {
      text: "Chart unavailable",
      subtext: message,
      left: "center",
      top: "middle",
      textStyle: {
        color: "#1f2328",
        fontSize: 16,
        fontWeight: 700,
      },
      subtextStyle: {
        color: "#656d76",
        fontSize: 12,
        width: 320,
        overflow: "break",
      },
    },
  });
}

async function ensureWorldMap() {
  if (worldMapLoaded) return;
  if (worldMapLoading) return worldMapLoading;
  worldMapLoading = fetch(
    "https://cdn.jsdelivr.net/npm/world-atlas@2/countries-110m.json",
  )
    .then((r) => r.json())
    .then((topoData) => {
      // Convert TopoJSON to GeoJSON for ECharts
      const countries = topojsonFeature(topoData, topoData.objects.countries);
      echarts.registerMap("world", countries);
      worldMapLoaded = true;
    })
    .catch(() => {
      // Fallback: try ECharts built-in world map URL
      return fetch("https://cdn.jsdelivr.net/npm/echarts@5/map/json/world.json")
        .then((r) => r.json())
        .then((geoJson) => {
          echarts.registerMap("world", geoJson);
          worldMapLoaded = true;
        });
    });
  return worldMapLoading;
}

// Minimal TopoJSON feature extraction (avoids importing topojson-client)
function topojsonFeature(topology, obj) {
  const arcs = topology.arcs;
  function decodeArc(arcIdx) {
    const arc = arcs[arcIdx < 0 ? ~arcIdx : arcIdx];
    const coords = [];
    let x = 0,
      y = 0;
    for (const [dx, dy] of arc) {
      x += dx;
      y += dy;
      coords.push([
        x * topology.transform.scale[0] + topology.transform.translate[0],
        y * topology.transform.scale[1] + topology.transform.translate[1],
      ]);
    }
    if (arcIdx < 0) coords.reverse();
    return coords;
  }
  function decodeRing(ring) {
    return ring.reduce((coords, arcIdx) => {
      const decoded = decodeArc(arcIdx);
      return coords.concat(decoded);
    }, []);
  }
  function decodeGeometry(geom) {
    if (geom.type === "Polygon") {
      return {
        type: "Polygon",
        coordinates: geom.arcs.map(decodeRing),
      };
    } else if (geom.type === "MultiPolygon") {
      return {
        type: "MultiPolygon",
        coordinates: geom.arcs.map((polygon) => polygon.map(decodeRing)),
      };
    }
    return geom;
  }
  const features = obj.geometries.map((geom) => ({
    type: "Feature",
    properties: geom.properties || {},
    geometry: decodeGeometry(geom),
  }));
  return { type: "FeatureCollection", features };
}

function buildEChartsOption(raw) {
  let parsed;
  try {
    parsed = typeof raw === "string" ? JSON.parse(raw) : raw;
  } catch {
    return null;
  }

  // Raw ECharts options mode (from RenderAdvancedChart)
  if (parsed.raw === true && parsed.options) {
    try {
      const opts =
        typeof parsed.options === "string"
          ? JSON.parse(parsed.options)
          : parsed.options;
      // Normalize country names in map series so they match the GeoJSON feature names
      normalizeMapSeriesData(opts);
      // Force white/light map styling to match page background
      applyMapDefaults(opts);
      // Mark as needing map registration
      opts._needsMap = needsMapRegistration(opts);
      return opts;
    } catch {
      return null;
    }
  }

  let dataArr;
  try {
    dataArr =
      typeof parsed.data === "string" ? JSON.parse(parsed.data) : parsed.data;
  } catch {
    return null;
  }

  let chartType = (parsed.type || "bar").toLowerCase();
  // Translate `horizontal_bar` to a regular bar with swapped axes — ECharts
  // has no native `horizontal_bar` series type, so without this we'd render
  // an empty chart (axes only, no bars). The LLM frequently emits this value.
  const isHorizontal = chartType === "horizontal_bar";
  if (isHorizontal) chartType = "bar";
  const title = parsed.title || "";
  const seriesName = parsed.seriesName || "";
  const xAxisName = parsed.xAxisName || "";
  const yAxisName = parsed.yAxisName || "";

  // Official Azure brand colors for data visualization
  const colors = [
    "#0078D4", // Azure Blue (primary)
    "#50E6FF", // Azure Cyan
    "#008575", // Azure Teal
    "#D83B01", // Azure Orange
    "#8661C5", // Azure Purple
    "#0063B1", // Azure Dark Blue
    "#00B7C3", // Azure Light Teal
    "#E3008C", // Azure Magenta
    "#FFB900", // Azure Yellow
    "#107C10", // Azure Green
    "#B4009E", // Azure Purple (alt)
    "#002050", // Azure Navy
    "#4F6BED", // Azure Indigo
    "#C239B3", // Azure Orchid
    "#767676", // Azure Gray
  ];

  if (chartType === "pie" || chartType === "funnel") {
    const pieData = dataArr.map((d) =>
      Array.isArray(d) ? { name: String(d[0]), value: d[1] } : d,
    );
    const total = pieData.reduce((sum, d) => sum + (Number(d.value) || 0), 0);

    // Weather-example technique: solid pie (radius 65%), selectedMode 'single'
    // so a slice pops out when clicked, hover shadow emphasis, and a bottom
    // legend listing the categories. Subtitle shows the total.
    return {
      title: {
        text: title,
        subtext:
          chartType === "pie" ? `Total ${total.toLocaleString()}` : undefined,
        left: "center",
        top: 0,
        textStyle: { fontSize: 14, color: "#1f2328" },
        subtextStyle: { fontSize: 11, color: "#656d76" },
      },
      tooltip: {
        trigger: "item",
        formatter: "{a} <br/>{b} : {c} ({d}%)",
      },
      legend: {
        bottom: 10,
        left: "center",
        type: "scroll",
        data: pieData.map((d) => d.name),
        textStyle: { color: "#656d76", fontSize: 11 },
      },
      color: colors,
      series: [
        {
          name: seriesName,
          type: chartType,
          radius: chartType === "pie" ? "65%" : undefined,
          center: ["50%", "52%"],
          selectedMode: chartType === "pie" ? "single" : undefined,
          data: pieData,
          label: {
            show: true,
            formatter: "{b}",
            color: "#1f2328",
            fontSize: 11,
          },
          labelLine: { show: true, length: 8, length2: 12 },
          emphasis: {
            itemStyle: {
              shadowBlur: 10,
              shadowOffsetX: 0,
              shadowColor: "rgba(0, 0, 0, 0.5)",
            },
          },
        },
      ],
    };
  }

  // bar, line, scatter
  const categories = dataArr.map((d) =>
    Array.isArray(d) ? String(d[0]) : d.name,
  );

  // X-axis label formatter: wrap long category names onto 2 lines so they're
  // readable without going horizontal. Splits on spaces, dashes, dots, slashes.
  // Falls back to a hard mid-string break if there is no separator.
  const wrapXLabel = (raw) => {
    const s = String(raw ?? "");
    if (s.length <= 14) return s;
    // Find the best mid-ish split character
    const seps = [/\s+/, /-/, /\./, /\//];
    for (const re of seps) {
      const parts = s.split(re);
      if (parts.length >= 2) {
        // Greedy 2-line wrap aiming for balance
        const half = Math.ceil(parts.length / 2);
        const a = parts.slice(0, half).join(" ");
        const b = parts.slice(half).join(" ");
        return `${a}\n${b}`;
      }
    }
    // Hard split
    const mid = Math.ceil(s.length / 2);
    return `${s.slice(0, mid)}\n${s.slice(mid)}`;
  };
  // Decide rotation: only rotate if the longest single label is huge AND many
  // categories — otherwise prefer the 2-line wrap which stays horizontal.
  const longest = categories.reduce((m, c) => Math.max(m, String(c).length), 0);
  const xRotate = categories.length > 14 && longest > 18 ? 30 : 0;
  const xAxisLabel = {
    fontSize: 11,
    color: "#1f2328",
    fontWeight: 500,
    interval: 0,
    rotate: xRotate,
    lineHeight: 13,
    formatter: wrapXLabel,
  };
  const yAxisLabel = { fontSize: 11, color: "#1f2328", fontWeight: 500 };

  // Detect multi-series: objects with keys beyond "name" and "value"
  const firstItem = dataArr[0];
  const isMultiSeries =
    firstItem &&
    !Array.isArray(firstItem) &&
    !("value" in firstItem) &&
    Object.keys(firstItem).filter((k) => k !== "name").length > 1;

  // Race line chart: multi-series line with end labels and animation
  if (chartType === "race" && isMultiSeries) {
    const seriesKeys = Object.keys(firstItem).filter((k) => k !== "name");
    return {
      animationDuration: 5000,
      title: {
        text: title,
        left: "center",
        textStyle: { fontSize: 14, color: "#1f2328" },
      },
      tooltip: { trigger: "axis", order: "valueDesc" },
      legend: {
        data: seriesKeys,
        bottom: 0,
        type: "scroll",
        textStyle: { color: "#656d76", fontSize: 11 },
      },
      color: colors,
      grid: { left: 60, right: 140, bottom: 40, top: 50 },
      xAxis: {
        type: "category",
        data: categories,
        name: xAxisName,
        nameLocation: "center",
        nameGap: 30,
        axisLabel: xAxisLabel,
      },
      yAxis: {
        type: "value",
        name: yAxisName,
        nameLocation: "center",
        nameGap: 45,
        axisLabel: yAxisLabel,
      },
      series: seriesKeys.map((key) => ({
        name: key,
        type: "line",
        showSymbol: false,
        data: dataArr.map((d) => d[key]),
        endLabel: {
          show: true,
          formatter: (params) => `${params.seriesName}: ${params.value}`,
          fontSize: 11,
        },
        labelLayout: { moveOverlap: "shiftY" },
        emphasis: { focus: "series" },
      })),
    };
  }

  if (isMultiSeries) {
    const seriesKeys = Object.keys(firstItem).filter((k) => k !== "name");
    return {
      title: {
        text: title,
        left: "center",
        textStyle: { fontSize: 14, color: "#1f2328" },
      },
      tooltip: { trigger: "axis" },
      legend: {
        data: seriesKeys,
        bottom: 0,
        textStyle: { color: "#656d76", fontSize: 11 },
      },
      color: colors,
      grid: { left: 60, right: 20, bottom: 40, top: 50 },
      xAxis: {
        type: "category",
        data: categories,
        name: xAxisName,
        nameLocation: "center",
        nameGap: 30,
        axisLabel: xAxisLabel,
      },
      yAxis: {
        type: "value",
        name: yAxisName,
        nameLocation: "center",
        nameGap: 45,
        axisLabel: yAxisLabel,
      },
      series: seriesKeys.map((key, idx) => ({
        name: key,
        type: chartType === "scatter" ? "scatter" : chartType,
        data: dataArr.map((d) => d[key]),
        barMaxWidth: 40,
      })),
    };
  }

  // Extract numeric values robustly. Accept any of:
  //   [name, value]                          (array form)
  //   {name, value}                           (canonical object form)
  //   {name, <seriesName>}                    (LLM-named value key, e.g. {name:'A', USD:100})
  // Without the third fallback, single-series bar charts whose value key
  // matches the seriesName render with empty bars.
  const values = dataArr.map((d) => {
    if (Array.isArray(d)) return d[1];
    if (d && "value" in d) return d.value;
    if (d && typeof d === "object") {
      const k = Object.keys(d).find((k) => k !== "name");
      return k ? d[k] : undefined;
    }
    return undefined;
  });

  // Horizontal bar: keep categories on the value-perpendicular axis, swap
  // which axis is `category` vs `value`. xAxisName/yAxisName already reflect
  // the user's intent (xAxis=USD, yAxis=Service for horizontal_bar).
  const categoryAxis = {
    type: "category",
    data: categories,
    name: isHorizontal ? yAxisName : xAxisName,
    nameLocation: "center",
    nameGap: isHorizontal ? 60 : 30,
    axisLabel: isHorizontal
      ? { fontSize: 11, color: "#1f2328", fontWeight: 500 }
      : xAxisLabel,
  };
  const valueAxis = {
    type: "value",
    name: isHorizontal ? xAxisName : yAxisName,
    nameLocation: "center",
    nameGap: isHorizontal ? 30 : 45,
    axisLabel: yAxisLabel,
  };

  return {
    title: {
      text: title,
      left: "center",
      textStyle: { fontSize: 14, color: "#1f2328" },
    },
    tooltip: { trigger: "axis" },
    color: colors,
    grid: { left: isHorizontal ? 120 : 60, right: 20, bottom: 40, top: 50 },
    xAxis: isHorizontal ? valueAxis : categoryAxis,
    yAxis: isHorizontal ? categoryAxis : valueAxis,
    series: [
      {
        name: seriesName,
        type: chartType === "scatter" ? "scatter" : chartType,
        data: values,
        barMaxWidth: 40,
      },
    ],
  };
}

function needsMapRegistration(opts) {
  if (!opts) return false;
  if (worldMapLoaded) return false;
  // Check for geo config (scatter on world map)
  if (opts.geo) {
    const geos = Array.isArray(opts.geo) ? opts.geo : [opts.geo];
    if (geos.some((g) => g.map === "world")) return true;
  }
  // Check for map series
  if (opts.series) {
    const series = Array.isArray(opts.series) ? opts.series : [opts.series];
    if (series.some((s) => s.type === "map" && s.map === "world")) return true;
  }
  return false;
}

function applyMapDefaults(opts) {
  if (!opts) return;
  // Light gray map styling — fits white chart card
  const lightArea = {
    areaColor: "#f0f0f0",
    borderColor: "#d0d7de",
  };
  if (opts.series) {
    const series = Array.isArray(opts.series) ? opts.series : [opts.series];
    for (const s of series) {
      if (s.type === "map") {
        s.itemStyle = { ...lightArea, ...(s.itemStyle || {}) };
      }
    }
  }
  if (opts.geo) {
    const geos = Array.isArray(opts.geo) ? opts.geo : [opts.geo];
    for (const g of geos) {
      g.itemStyle = { ...lightArea, ...(g.itemStyle || {}) };
    }
  }
  if (!opts.backgroundColor) {
    opts.backgroundColor = "transparent";
  }
}

// Azure brand palette — keeps the existing white/Azure-blue identity.
// Wow comes from gradients, glow, smooth lines, hover effects and animations,
// not from a different palette.
const WOW_PALETTE = [
  "#0078D4", // Azure Blue (primary)
  "#50E6FF", // Azure Cyan
  "#008575", // Azure Teal
  "#8661C5", // Azure Purple
  "#0063B1", // Azure Dark Blue
  "#00B7C3", // Azure Light Teal
  "#D83B01", // Azure Orange
  "#107C10", // Azure Green
  "#E3008C", // Azure Magenta
  "#FFB900", // Azure Yellow
  "#4F6BED", // Azure Indigo
  "#002050", // Azure Navy
];
const WOW_GRADIENT_STOPS = {
  "#0078D4": ["#50A8F5", "#0078D4"],
  "#50E6FF": ["#9DF1FF", "#00B7C3"],
  "#008575": ["#26C0AD", "#008575"],
  "#8661C5": ["#B49BE0", "#8661C5"],
  "#0063B1": ["#3B8DD5", "#0063B1"],
  "#00B7C3": ["#5DD8E0", "#00B7C3"],
  "#D83B01": ["#FF7A3D", "#D83B01"],
  "#107C10": ["#4CB14C", "#107C10"],
  "#E3008C": ["#F260B6", "#E3008C"],
  "#FFB900": ["#FFD75A", "#FFB900"],
  "#4F6BED": ["#8295F2", "#4F6BED"],
  "#002050": ["#1E4A8C", "#002050"],
};

function wowGradient(hex, vertical = true) {
  const stops = WOW_GRADIENT_STOPS[hex] || [hex, hex];
  // ECharts LinearGradient signature: (x0, y0, x1, y1) in 0..1 space
  // vertical: top → bottom (0,0)→(0,1)
  // horizontal: left → right (0,0)→(1,0)
  const x0 = 0;
  const y0 = 0;
  const x1 = vertical ? 0 : 1;
  const y1 = vertical ? 1 : 0;
  return new echarts.graphic.LinearGradient(x0, y0, x1, y1, [
    { offset: 0, color: stops[0] },
    { offset: 1, color: stops[1] },
  ]);
}

function wowAreaGradient(hex) {
  return new echarts.graphic.LinearGradient(0, 0, 0, 1, [
    { offset: 0, color: hex + "66" },
    { offset: 1, color: hex + "00" },
  ]);
}

// Wow theme overlay applied to every ECharts option just before setOption.
// Keeps the existing white/Azure-blue look but adds:
//  - vertical gradient bars
//  - smoothed lines with soft glow + area gradients
//  - pie hover lift + scale
//  - rich axis-pointer tooltip with crosshair
//  - staggered entry animations on every series
function applyWowTheme(opts) {
  if (!opts || typeof opts !== "object") return;
  const ink = "#1f2328";
  const inkDim = "#656d76";
  const inkMute = "#8b96a0";
  const grid = "#eef0f3";
  const tooltipBg = "rgba(255,255,255,0.98)";

  if (opts.backgroundColor === undefined) opts.backgroundColor = "transparent";

  // Override the default palette with Azure brand colors only if not customised
  if (!opts.color || (Array.isArray(opts.color) && opts.color.length <= 1)) {
    opts.color = [...WOW_PALETTE];
  }

  if (opts.title) {
    const titles = Array.isArray(opts.title) ? opts.title : [opts.title];
    for (const t of titles) {
      t.textStyle = {
        color: ink,
        fontWeight: 600,
        fontSize: 14,
        ...(t.textStyle || {}),
      };
      if (t.subtextStyle)
        t.subtextStyle = { color: inkMute, ...t.subtextStyle };
    }
  }
  if (opts.legend) {
    const legends = Array.isArray(opts.legend) ? opts.legend : [opts.legend];
    for (const l of legends) {
      l.textStyle = {
        color: inkDim,
        fontSize: 11,
        ...(l.textStyle || {}),
      };
      l.pageTextStyle = { color: inkDim, ...(l.pageTextStyle || {}) };
      l.icon = l.icon || "circle";
      l.itemWidth = l.itemWidth || 8;
      l.itemHeight = l.itemHeight || 8;
      l.inactiveColor = l.inactiveColor || "rgba(101,109,118,0.35)";
    }
  }
  if (opts.tooltip) {
    const tts = Array.isArray(opts.tooltip) ? opts.tooltip : [opts.tooltip];
    for (const tt of tts) {
      tt.backgroundColor = tt.backgroundColor || tooltipBg;
      tt.borderColor = tt.borderColor || "rgba(0,120,212,0.35)";
      tt.borderWidth = tt.borderWidth ?? 1;
      tt.padding = tt.padding ?? 10;
      tt.textStyle = {
        color: ink,
        fontSize: 12,
        ...(tt.textStyle || {}),
      };
      tt.extraCssText =
        tt.extraCssText ||
        "backdrop-filter:blur(8px);box-shadow:0 8px 28px rgba(0,32,80,0.18);border-radius:8px;";
      // Add axis pointer crosshair on cartesian charts
      if (tt.trigger === "axis" && !tt.axisPointer) {
        tt.axisPointer = {
          type: "line",
          lineStyle: {
            color: "rgba(0,120,212,0.35)",
            width: 1,
            type: "dashed",
          },
          crossStyle: { color: "rgba(0,120,212,0.35)" },
        };
      }
    }
  }
  const axes = ["xAxis", "yAxis", "radiusAxis", "angleAxis"];
  for (const ak of axes) {
    if (!opts[ak]) continue;
    const arr = Array.isArray(opts[ak]) ? opts[ak] : [opts[ak]];
    for (const ax of arr) {
      ax.nameTextStyle = {
        color: inkDim,
        fontSize: 10,
        ...(ax.nameTextStyle || {}),
      };
      ax.axisLabel = {
        color: inkDim,
        fontSize: 10,
        ...(ax.axisLabel || {}),
      };
      ax.axisLine = {
        ...(ax.axisLine || {}),
        lineStyle: {
          color: "#d0d7de",
          ...((ax.axisLine || {}).lineStyle || {}),
        },
      };
      ax.splitLine = {
        ...(ax.splitLine || {}),
        lineStyle: {
          color: grid,
          type: "dashed",
          ...((ax.splitLine || {}).lineStyle || {}),
        },
      };
      ax.axisTick = {
        ...(ax.axisTick || {}),
        lineStyle: {
          color: "#d0d7de",
          ...((ax.axisTick || {}).lineStyle || {}),
        },
      };
    }
  }
  if (opts.visualMap) {
    const vms = Array.isArray(opts.visualMap)
      ? opts.visualMap
      : [opts.visualMap];
    for (const v of vms) {
      v.textStyle = { color: inkDim, ...(v.textStyle || {}) };
    }
  }
  if (opts.radar) {
    const radars = Array.isArray(opts.radar) ? opts.radar : [opts.radar];
    for (const r of radars) {
      r.axisName = {
        color: inkDim,
        fontSize: 11,
        ...(r.axisName || {}),
      };
      r.splitLine = {
        lineStyle: { color: grid },
        ...(r.splitLine || {}),
      };
      r.splitArea = {
        areaStyle: {
          color: ["#f8fafc", "#ffffff"],
        },
        ...(r.splitArea || {}),
      };
      r.axisLine = {
        lineStyle: { color: "#d0d7de" },
        ...(r.axisLine || {}),
      };
    }
  }
  // Calendar/heatmap
  if (opts.calendar) {
    const cals = Array.isArray(opts.calendar) ? opts.calendar : [opts.calendar];
    for (const c of cals) {
      c.itemStyle = {
        borderColor: grid,
        color: "#fafbfc",
        ...(c.itemStyle || {}),
      };
      c.dayLabel = { color: inkDim, ...(c.dayLabel || {}) };
      c.monthLabel = { color: inkDim, ...(c.monthLabel || {}) };
      c.yearLabel = { color: ink, ...(c.yearLabel || {}) };
    }
  }
  // Series-level wow: gradients, glow, elastic stagger, rich emphasis
  if (opts.series) {
    const arr = Array.isArray(opts.series) ? opts.series : [opts.series];
    arr.forEach((s, idx) => {
      const baseColor = pickSeriesColor(s, idx, opts.color);
      decorateSeries(s, baseColor, idx);
    });
  }
}

function pickSeriesColor(s, idx, palette) {
  // Honor an explicit per-series color if the LLM set one
  const explicit =
    (s.itemStyle && s.itemStyle.color) || (s.lineStyle && s.lineStyle.color);
  if (typeof explicit === "string") return explicit;
  const arr = Array.isArray(palette) ? palette : WOW_PALETTE;
  return arr[idx % arr.length];
}

function decorateSeries(s, baseColor, idx) {
  if (!s || typeof s !== "object") return;
  // Animation defaults (gentle stagger)
  if (s.animationDuration === undefined) s.animationDuration = 1100;
  if (s.animationEasing === undefined) s.animationEasing = "cubicOut";
  if (s.animationDelay === undefined) {
    s.animationDelay = (i) => i * 50 + idx * 70;
  }
  if (s.animationDurationUpdate === undefined) s.animationDurationUpdate = 600;

  const t = s.type;
  if (t === "bar") {
    s.itemStyle = {
      color: wowGradient(baseColor, true),
      borderRadius: [6, 6, 0, 0],
      shadowBlur: 8,
      shadowColor: baseColor + "33",
      shadowOffsetY: 2,
      ...(s.itemStyle || {}),
    };
    s.barMaxWidth = s.barMaxWidth || 44;
    s.emphasis = {
      focus: "series",
      itemStyle: {
        shadowBlur: 16,
        shadowColor: baseColor + "88",
      },
      ...(s.emphasis || {}),
    };
  } else if (t === "line") {
    s.lineStyle = {
      width: 3,
      shadowBlur: 6,
      shadowColor: baseColor + "66",
      shadowOffsetY: 2,
      ...(s.lineStyle || {}),
    };
    s.itemStyle = {
      color: baseColor,
      borderColor: "#ffffff",
      borderWidth: 2,
      ...(s.itemStyle || {}),
    };
    s.symbol = s.symbol || "circle";
    s.symbolSize = s.symbolSize ?? 6;
    if (s.smooth === undefined) s.smooth = true;
    if (s.areaStyle === undefined) {
      // Subtle area fill by default for line charts
      s.areaStyle = { color: wowAreaGradient(baseColor) };
    } else if (s.areaStyle) {
      s.areaStyle = {
        color: wowAreaGradient(baseColor),
        ...s.areaStyle,
      };
    }
    s.emphasis = {
      focus: "series",
      scale: 1.35,
      itemStyle: {
        shadowBlur: 12,
        shadowColor: baseColor,
      },
      ...(s.emphasis || {}),
    };
  } else if (t === "pie") {
    s.itemStyle = {
      borderColor: "#ffffff",
      borderWidth: 2,
      borderRadius: 6,
      shadowBlur: 8,
      shadowColor: "rgba(0,120,212,0.18)",
      ...(s.itemStyle || {}),
    };
    if (!s.radius) s.radius = ["45%", "70%"];
    s.label = {
      color: "#656d76",
      fontSize: 11,
      ...(s.label || {}),
    };
    s.labelLine = {
      lineStyle: { color: "#d0d7de" },
      ...(s.labelLine || {}),
    };
    s.emphasis = {
      scaleSize: 10,
      itemStyle: {
        shadowBlur: 18,
        shadowColor: "rgba(0,120,212,0.4)",
      },
      label: { show: true, fontSize: 14, fontWeight: 600, color: "#1f2328" },
      ...(s.emphasis || {}),
    };
  } else if (t === "scatter" || t === "effectScatter") {
    s.itemStyle = {
      shadowBlur: 6,
      shadowColor: baseColor + "66",
      ...(s.itemStyle || {}),
    };
    s.emphasis = {
      scale: 1.5,
      itemStyle: { shadowBlur: 14, shadowColor: baseColor },
      ...(s.emphasis || {}),
    };
  } else if (t === "funnel") {
    s.itemStyle = {
      borderColor: "#ffffff",
      borderWidth: 2,
      ...(s.itemStyle || {}),
    };
  } else if (t === "map") {
    s.emphasis = {
      itemStyle: {
        areaColor: "rgba(0,120,212,0.25)",
        borderColor: "#0078D4",
        shadowBlur: 8,
        shadowColor: "rgba(0,120,212,0.4)",
      },
      label: { color: "#1f2328", fontWeight: 600 },
      ...(s.emphasis || {}),
    };
  } else if (t === "heatmap") {
    s.itemStyle = {
      borderColor: "#ffffff",
      borderWidth: 1,
      ...(s.itemStyle || {}),
    };
    s.emphasis = {
      itemStyle: { shadowBlur: 10, shadowColor: "rgba(0,120,212,0.5)" },
      ...(s.emphasis || {}),
    };
  } else if (t === "treemap") {
    s.itemStyle = {
      borderColor: "#ffffff",
      borderWidth: 2,
      gapWidth: 2,
      ...(s.itemStyle || {}),
    };
    s.label = {
      color: "#fff",
      textShadowColor: "rgba(0,0,0,0.4)",
      textShadowBlur: 3,
      ...(s.label || {}),
    };
  } else if (t === "radar") {
    s.lineStyle = {
      width: 2,
      shadowBlur: 6,
      shadowColor: baseColor + "66",
      ...(s.lineStyle || {}),
    };
    s.areaStyle = {
      opacity: 0.2,
      color: baseColor,
      ...(s.areaStyle || {}),
    };
    s.symbol = s.symbol || "circle";
    s.symbolSize = s.symbolSize ?? 5;
  } else if (t === "gauge") {
    s.progress = {
      show: true,
      width: 10,
      itemStyle: {
        color: wowGradient(baseColor, false),
        shadowBlur: 8,
        shadowColor: baseColor + "88",
      },
      ...(s.progress || {}),
    };
    s.axisLine = {
      lineStyle: {
        width: 10,
        color: [[1, "#eef0f3"]],
      },
      ...(s.axisLine || {}),
    };
  }
}

function mountChart(el, chartData) {
  if (!el || el._echarts_mounted) return;
  el._echarts_mounted = true;
  const option = buildEChartsOption(chartData);
  if (!option) return;

  const doMount = () => {
    nextTick(() => {
      const isMap =
        option._needsMap ||
        !!option.geo ||
        (option.series &&
          Array.isArray(option.series) &&
          option.series.some((s) => s.type === "map"));
      delete option._needsMap;
      if (isMap) {
        el.style.height = "520px";
      }
      const instance = echarts.init(el, null, {
        renderer: isMap ? "canvas" : "svg",
      });
      applyWowTheme(option);
      instance.setOption(option);
      // Log chart rendering to App Insights
      const seriesTypes = (option.series || []).map((s) => s.type).join(",");
      const dataPointCount = (option.series || []).reduce(
        (sum, s) => sum + (Array.isArray(s.data) ? s.data.length : 0),
        0,
      );
      window.__trackAppInsightsTrace?.(
        `Chart rendered: isMap=${isMap} seriesTypes=${seriesTypes} dataPoints=${dataPointCount}`,
        {
          isMap: String(isMap),
          seriesTypes,
          dataPoints: String(dataPointCount),
        },
      );
      chartInstances.push(instance);
      const ro = new ResizeObserver(() => instance.resize());
      ro.observe(el);
    });
  };

  if (option._needsMap) {
    ensureWorldMap()
      .then(doMount)
      .catch((error) => {
        window.__trackAppInsightsException?.(error, {
          source: "echarts.world-map",
        });
        replaceWithChartFallback(
          option,
          "World map data could not be loaded. Check browser network and CSP settings.",
        );
        doMount();
      });
  } else {
    doMount();
  }
}

onBeforeUnmount(() => {
  chartInstances.forEach((c) => c.dispose());
  document.removeEventListener("click", dismissPopover);
});

// ── Aggregated tool calls for sidebar ──
// Dedupe by tool-call id. The same tool call can appear in both the
// persisted message history (loaded via /messages on session resume) AND
// in the live stream's toolCalls list when the user navigates away from
// a running session and back. Stream entries reflect the freshest state
// (running spinner, latest result), so they win.
const allToolCalls = computed(() => {
  const byId = new Map();
  const order = [];
  const add = (tc, uidPrefix) => {
    const id = tc.id;
    if (id == null) {
      // Anonymous tool call — keep as-is, no dedupe possible.
      const entry = { ...tc, _uid: `${uidPrefix}-anon-${order.length}` };
      order.push(entry);
      return;
    }
    if (byId.has(id)) {
      // Stream wins over history — overwrite in place, keep ordering stable.
      const idx = byId.get(id);
      order[idx] = { ...tc, _uid: `${uidPrefix}-${id}` };
    } else {
      const entry = { ...tc, _uid: `${uidPrefix}-${id}` };
      byId.set(id, order.length);
      order.push(entry);
    }
  };
  for (const msg of messages.value) {
    if (!msg.toolCalls) continue;
    for (const tc of msg.toolCalls) add(tc, "msg");
  }
  for (const tc of streamToolCalls.value) add(tc, "stream");
  // Append any live cooling-down ghosts at the end so they pin to the top of
  // the reversed sidebar view. They are ephemeral and never get committed.
  // IMPORTANT: pass the original reference (no spread) so toggling
  // tc.expanded in the template mutates the reactive source.
  for (const c of streamCoolers.value) {
    c._uid = c._uid || `cool-${crypto.randomUUID()}`;
    order.push(c);
  }
  return order;
});

// -- Connected sources --
// (moved to reactive state: publicSources, azureApis)

function formatJson(str) {
  try {
    return JSON.stringify(JSON.parse(str), null, 2);
  } catch {
    return str;
  }
}

function highlightJson(str) {
  if (!str) return "";
  const raw = String(str);
  // Try strict JSON pretty-print first
  let pretty = raw;
  try {
    pretty = JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    // Not valid JSON — try to extract the first JSON object/array within
    const match = raw.match(/[\[{][\s\S]*[\]}]/);
    if (match) {
      try {
        pretty = JSON.stringify(JSON.parse(match[0]), null, 2);
      } catch {
        // leave as raw
      }
    }
  }
  // Highlight as JSON if it now looks like JSON, otherwise plain escape
  const looksLikeJson = /^[\s]*[\[{]/.test(pretty);
  if (looksLikeJson) {
    try {
      return hljs.highlight(pretty, { language: "json", ignoreIllegals: true })
        .value;
    } catch {
      // fall through
    }
  }
  return pretty
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function truncate(str, max) {
  if (!str || str.length <= max) return str;
  return str.slice(0, max) + "\n... (truncated)";
}

function scrollToBottom() {
  nextTick(() => {
    if (messagesEl.value) {
      messagesEl.value.scrollTo({
        top: messagesEl.value.scrollHeight,
        behavior: "smooth",
      });
    }
  });
}

watch(() => messages.value.length, scrollToBottom);
watch(streamBuffer, scrollToBottom);
watch(() => streamToolCalls.value.length, scrollToBottom);

watch(streaming, async (val) => {
  if (!val) {
    await nextTick();
    inputEl.value?.focus();
  }
});

// ── Wow markdown tables ──
// Builds a glassmorphism table styled like the FinOps Cosmos reference:
//  - uppercase letter-spaced headers
//  - mono numerics, hover row tint
//  - auto-color delta cells (▲/▼/+/-N%) green/red/orange
//  - status pills (OK / Watch / Alert / Optimal / Critical)
//  - mini gradient bar in the rightmost numeric column showing relative magnitude
function buildWowTable(headerCells, dataRows) {
  const colCount = headerCells.length;
  // Detect numeric columns and find rightmost numeric column for the mini bar
  const numericCounts = new Array(colCount).fill(0);
  const totalRows = dataRows.length;
  const colNumericValues = new Array(colCount).fill(0).map(() => []);
  for (const row of dataRows) {
    for (let c = 0; c < colCount; c++) {
      const cell = row[c] || "";
      const n = parseNumeric(cell);
      if (n !== null) {
        numericCounts[c]++;
        colNumericValues[c].push(Math.abs(n));
      }
    }
  }
  const numericColumns = numericCounts.map(
    (n, c) => n > 0 && n / Math.max(1, totalRows) >= 0.6,
  );
  // Pick rightmost numeric column for mini bar
  let barColumn = -1;
  for (let c = colCount - 1; c >= 0; c--) {
    if (numericColumns[c]) {
      barColumn = c;
      break;
    }
  }
  const barColMax =
    barColumn >= 0 ? Math.max(1, ...colNumericValues[barColumn]) : 1;

  const headHtml = headerCells
    .map(
      (h, c) =>
        `<th class="${numericColumns[c] ? "wt-num" : ""}">${escapeHtml(h)}</th>`,
    )
    .join("");

  const bodyHtml = dataRows
    .map((row) => {
      const cells = headerCells
        .map((_, c) => {
          const raw = row[c] ?? "";
          const isNum = numericColumns[c];
          const inner = enhanceCell(raw);
          return `<td class="${isNum ? "wt-num" : ""}">${inner}</td>`;
        })
        .join("");
      return `<tr>${cells}</tr>`;
    })
    .join("");
  return `<div class="wt-wrap"><table class="wow-table"><thead><tr>${headHtml}</tr></thead><tbody>${bodyHtml}</tbody></table></div>`;
}

function parseNumeric(cell) {
  if (!cell) return null;
  // Strip $, %, commas, leading +/▲/▼, trailing K/M/B
  const m = String(cell)
    .replace(/[▲▼↑↓]/g, "")
    .match(/-?[\d,]+\.?\d*\s*[kKmMbB%]?/);
  if (!m) return null;
  let raw = m[0].replace(/,/g, "").trim();
  let mult = 1;
  if (/k$/i.test(raw)) {
    mult = 1e3;
    raw = raw.slice(0, -1);
  } else if (/m$/i.test(raw)) {
    mult = 1e6;
    raw = raw.slice(0, -1);
  } else if (/b$/i.test(raw)) {
    mult = 1e9;
    raw = raw.slice(0, -1);
  } else if (/%$/.test(raw)) {
    raw = raw.slice(0, -1);
  }
  const n = parseFloat(raw);
  return isFinite(n) ? n * mult : null;
}

function enhanceCell(raw) {
  const safe = escapeHtml(raw);
  // Status pills
  const trimmed = raw.trim();
  const lower = trimmed.toLowerCase();
  const goodWords = [
    "ok",
    "optimal",
    "healthy",
    "good",
    "pass",
    "✓",
    "yes",
    "active",
  ];
  const warnWords = [
    "watch",
    "warning",
    "warn",
    "caution",
    "pending",
    "medium",
  ];
  const badWords = [
    "alert",
    "critical",
    "fail",
    "failed",
    "error",
    "high",
    "✗",
    "no",
    "down",
    "orphan",
    "orphaned",
    "unattached",
    "idle",
  ];
  if (goodWords.includes(lower))
    return `<span class="wt-tag wt-tag-g">${safe}</span>`;
  if (warnWords.includes(lower))
    return `<span class="wt-tag wt-tag-w">${safe}</span>`;
  if (badWords.includes(lower))
    return `<span class="wt-tag wt-tag-b">${safe}</span>`;

  // Delta arrows  ▲ +14%   ▼ -8%   — flat
  const upMatch = trimmed.match(/^(?:▲|↑|\+)\s*([\d.,]+\s*%?)$/);
  if (upMatch) return `<span class="wt-up">▲ ${escapeHtml(upMatch[1])}</span>`;
  const downMatch = trimmed.match(/^(?:▼|↓|-)\s*([\d.,]+\s*%?)$/);
  if (downMatch)
    return `<span class="wt-down">▼ ${escapeHtml(downMatch[1])}</span>`;
  if (/^(—|flat|n\/a|-)$/i.test(trimmed))
    return `<span class="wt-flat">—</span>`;

  // Currency / percent stays as-is but rendered via mono in CSS
  return safe;
}

function escapeHtml(s) {
  return String(s)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function renderContent(text) {
  if (!text) return "";
  let html = text.replace(
    /```(\w*)\n([\s\S]*?)```/g,
    '<pre><code class="lang-$1">$2</code></pre>',
  );
  html = html.replace(
    /((?:^|\n)\|.+\|(?:\n\|[-:| ]+\|)(?:\n\|.+\|)+)/g,
    (match) => {
      const lines = match.trim().split("\n");
      if (lines.length < 2) return match;
      // Strip outer pipes then split — keeps interior empty cells aligned.
      const splitRow = (line) =>
        line
          .trim()
          .replace(/^\||\|$/g, "")
          .split("|")
          .map((c) => c.trim());
      const headerCells = splitRow(lines[0]);
      const dataRows = lines.slice(2).map(splitRow);
      return buildWowTable(headerCells, dataRows);
    },
  );
  html = html.replace(/^### (.+)$/gm, "<h4>$1</h4>");
  html = html.replace(/^## (.+)$/gm, "<h3>$1</h3>");
  html = html.replace(/^# (.+)$/gm, "<h2>$1</h2>");
  html = html.replace(/`([^`]+)`/g, "<code>$1</code>");
  html = html.replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>");
  html = html.replace(/\*(.+?)\*/g, "<em>$1</em>");
  html = html.replace(/^- (.+)$/gm, "<li>$1</li>");
  html = html.replace(/((?:<li>.*<\/li>\n?)+)/g, "<ul>$1</ul>");
  html = html.replace(/\n/g, "<br/>");
  html = html.replace(/<\/(table|pre|ul|h[234])><br\/>/g, "</$1>");
  html = html.replace(/<br\/><(table|pre|ul|h[234])/g, "<$1");
  return html;
}

function sendPrompt(text) {
  if (!props.user || clearing.value) return;
  input.value = text;
  send();
}

async function requestAnalyze() {
  // Wait for any in-flight uploads to finish so the prompt always sees the file.
  const pending = () => attachments.value.some((a) => !a.fileId && !a.error);
  if (pending()) {
    const start = Date.now();
    while (pending() && Date.now() - start < 30000) {
      await new Promise((r) => setTimeout(r, 100));
    }
  }
  const list = readyAttachments.value;
  if (list.length) {
    const names = list.map((a) => `'${a.fileName}'`).join(", ");
    input.value = `Analyze the uploaded file${list.length > 1 ? "s" : ""} ${names}. Find the biggest cost waste and tell me exactly what to do about it.`;
  } else {
    input.value =
      "Find the biggest cost waste and tell me what to do about it.";
  }
  send();
}

function requestPresentation() {
  input.value =
    "Build a 6-slide deck for my FinOps team: where we're wasting money and the fixes the agent will run for us.";
  send();
}

function requestScript() {
  input.value =
    "Based on our conversation, generate an Azure CLI script to implement the FinOps recommendations we discussed. Ask me to confirm the specific actions before generating the script. If there are no actionable recommendations yet, let me know.";
  send();
}

function copyScript(content) {
  navigator.clipboard.writeText(content).catch(() => {});
}

async function send() {
  if (!props.user || clearing.value) return;
  const prompt = input.value.trim();
  if (!prompt) return;
  // Per-session gate: only block sending another prompt to the SAME session
  // while it is already streaming. Other sessions can keep running in the
  // background.
  const startSessionId = currentSessionId.value;
  if (runningSessions.has(startSessionId || "__pending__")) return;

  messages.value.push({ role: "user", content: prompt });
  input.value = "";
  nextTick(() => {
    if (inputEl.value) inputEl.value.style.height = "auto";
  });
  // Track this stream's session id locally so we can detect (a) when the
  // user has navigated to a different session mid-stream, and (b) which
  // entry to drop from runningSessions when we finish.
  let streamingId = startSessionId || "__pending__";
  runningSessions.add(streamingId);
  const isActiveView = () =>
    streamingId === (currentSessionId.value || "__pending__");
  streamBuffer.value = "";
  activeTools.value = [];
  scrollToBottom();

  abortController = new AbortController();
  // Per-stream buckets live in perSessionToolCalls / perSessionCharts keyed
  // by streamingId. Writing through the map (instead of a closure-local
  // array) means the right-rail UI — driven by the streamToolCalls /
  // streamCharts computeds — picks up new tool events even for backgrounded
  // sessions, and the data survives the user switching away and back.
  const ensureBuckets = (sid) => {
    if (!perSessionToolCalls.has(sid)) perSessionToolCalls.set(sid, []);
    if (!perSessionCharts.has(sid)) perSessionCharts.set(sid, []);
  };
  ensureBuckets(streamingId);
  let toolCalls = perSessionToolCalls.get(streamingId);
  let charts = perSessionCharts.get(streamingId);
  let hasDeltas = false;

  // === TIMING HOOKS ===
  // Captures every meaningful moment of the turn so we can build a flat
  // timing table afterwards. Records: client send, each SSE event arrival
  // (with type/tool), backend phase timings (token.*, session.*, sdk.*,
  // chat.total), tool durations from tool_done.durationMs, and 429 retries
  // from cooling_down. Dumped to console.table + window.__lastTimings
  // when the stream ends.
  const t0 = performance.now();
  const timings = [];
  const recordTiming = (entry) => {
    timings.push({ t_ms: Math.round(performance.now() - t0), ...entry });
  };
  recordTiming({ kind: "client", phase: "send.start", prompt: prompt.slice(0, 60) });

  try {
    const res = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        prompt,
        model: selectedModel.value,
        sessionId: currentSessionId.value || undefined,
      }),
      signal: abortController.signal,
    });

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buf = "";

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buf += decoder.decode(value, { stream: true });
      const lines = buf.split("\n");
      buf = lines.pop() || "";

      for (const line of lines) {
        if (!line.startsWith("data: ")) continue;
        const raw = line.slice(6);
        if (raw === "[DONE]") break;

        let data;
        try {
          data = JSON.parse(raw);
        } catch {
          continue;
        }

        // Capture every SSE event's arrival timestamp for the timing table.
        // Backend-emitted `timing` events are stored verbatim (they carry their
        // own ms measurement); all other events get a client-arrival t_ms.
        if (data.type === "timing") {
          recordTiming({ kind: "server", phase: data.phase, server_ms: data.ms, extra: data.extra });
        } else if (data.type === "tool_done") {
          recordTiming({ kind: "event", phase: "tool_done", tool: data.tool, durationMs: data.durationMs, success: data.success });
        } else if (data.type === "tool_start") {
          recordTiming({ kind: "event", phase: "tool_start", tool: data.tool });
        } else if (data.type === "cooling_down") {
          // eslint-disable-next-line no-console
          console.log("[SSE cooling_down arrived]", data);
          recordTiming({ kind: "event", phase: "cooling_down", attempt: data.attempt, waitSec: data.waitSeconds });
        } else if (data.type === "delta") {
          if (!timings.some((x) => x.phase === "first_delta")) {
            recordTiming({ kind: "event", phase: "first_delta" });
          }
        } else if (data.type !== "message") {
          recordTiming({ kind: "event", phase: data.type });
        }

        // Routing/sidebar events are handled regardless of which session the
        // user is currently viewing. Everything else is gated on isActiveView()
        // below so a backgrounded stream keeps draining bytes (the server
        // needs us to keep reading) without polluting the foreground view.
        const routingEvent =
          data.type === "session" || data.type === "session_title" || data.type === "timing";
        // Tool / chart / completion-marker events go into the per-session
        // map so a backgrounded stream still accumulates state and the user
        // sees a complete tool list when they switch back. Only delta/text
        // and intent ticker events are gated on the active view (their UI
        // lives in shared single-value refs that can only show one stream).
        const sessionScopedEvent =
          data.type === "tool_start" ||
          data.type === "tool_done" ||
          data.type === "cooling_down" ||
          data.type === "chart" ||
          data.type === "html_ready" ||
          data.type === "script_ready" ||
          data.type === "maturity_score";
        if (!routingEvent && !sessionScopedEvent && !isActiveView()) continue;

        switch (data.type) {
          case "session":
            // Backend echoes the active sessionId at the start of each chat.
            // Swap our local streamingId/runningSessions key from the
            // "__pending__" sentinel (or stale id) to the real id so the
            // pulsing dot follows the right row.
            if (data.id) {
              if (streamingId !== data.id) {
                runningSessions.delete(streamingId);
                // Migrate any tool/chart events that arrived under the
                // "__pending__" sentinel into the real session bucket so
                // the right-rail UI stays continuous across the id swap.
                const pendingTools = perSessionToolCalls.get(streamingId);
                const pendingCharts = perSessionCharts.get(streamingId);
                if (pendingTools && pendingTools.length) {
                  perSessionToolCalls.set(data.id, pendingTools);
                }
                if (pendingCharts && pendingCharts.length) {
                  perSessionCharts.set(data.id, pendingCharts);
                }
                perSessionToolCalls.delete(streamingId);
                perSessionCharts.delete(streamingId);
                streamingId = data.id;
                runningSessions.add(streamingId);
                ensureBuckets(streamingId);
                toolCalls = perSessionToolCalls.get(streamingId);
                charts = perSessionCharts.get(streamingId);
              }
              // Only move the user's view to the new id if they haven't
              // navigated away to a different session in the meantime.
              if (
                currentSessionId.value === null ||
                currentSessionId.value === startSessionId
              ) {
                currentSessionId.value = data.id;
              }
            }
            break;

          case "session_title": {
            const idx = sessions.value.findIndex((s) => s.id === data.id);
            if (idx >= 0) {
              const updated = { ...sessions.value[idx], summary: data.title };
              sessions.value = [
                ...sessions.value.slice(0, idx),
                updated,
                ...sessions.value.slice(idx + 1),
              ];
            } else {
              // New session not yet in sidebar — prepend a stub row.
              sessions.value = [
                {
                  id: data.id,
                  summary: data.title,
                  modifiedTime: new Date().toISOString(),
                },
                ...sessions.value,
              ];
            }
            break;
          }

          case "delta":
            clearInterval(intentAnimTimer);
            intentAnimTimer = null;
            streamIntent.value = "";
            enqueueText(data.content);
            hasDeltas = true;
            break;

          case "message":
            if (data.content && !hasDeltas) enqueueText(data.content);
            break;

          case "tool_start":
            // If we already streamed text BEFORE this tool call, that text was
            // mid-turn narration ("I'm rerunning…", "I've got the estate shape…")
            // — not the final answer. Wipe it; the real answer streams after the
            // last tool completes.
            if (hasDeltas) {
              console.log(
                "[tool_start wipe] streamBuffer length before wipe=",
                streamBuffer.value.length,
                "first 60=",
                streamBuffer.value.slice(0, 60),
              );
              if (textAnimFrame) {
                cancelAnimationFrame(textAnimFrame);
                textAnimFrame = null;
              }
              pendingText = "";
              streamBuffer.value = "";
              hasDeltas = false;
            }
            activeTools.value = [...activeTools.value, data.tool];
            {
              const existingIdx = toolCalls.findIndex((t) => t.id === data.id);
              if (existingIdx >= 0) {
                console.warn(
                  "[tool_start] duplicate id, skipping push",
                  data.id,
                  data.tool,
                  data.args,
                );
              } else {
                console.log(
                  "[tool_start]",
                  data.id,
                  data.tool,
                  typeof data.args === "string"
                    ? data.args.slice(0, 120)
                    : JSON.stringify(data.args || {}).slice(0, 120),
                );
                toolCalls.push({
                  id: data.id,
                  tool: data.tool,
                  args: data.args || null,
                  result: null,
                  error: null,
                  success: null,
                  durationMs: null,
                  done: false,
                  expanded: false,
                });
              }
            }
            // Reseat the map entry so the reactive Map notifies — the
            // streamToolCalls computed re-derives off this map, so all
            // viewers of this session (now or later) see the update.
            perSessionToolCalls.set(streamingId, [...toolCalls]);
            toolCalls = perSessionToolCalls.get(streamingId);
            if (data.tool === "report_intent" && data.args) {
              try {
                const parsed =
                  typeof data.args === "string"
                    ? JSON.parse(data.args)
                    : data.args;
                if (parsed.intent) {
                  clearInterval(intentAnimTimer);
                  streamIntent.value = "";
                  let i = 0;
                  const txt = parsed.intent;
                  intentAnimTimer = setInterval(() => {
                    i++;
                    streamIntent.value =
                      i >= txt.length ? txt + "…" : txt.slice(0, i) + "…";
                    if (i >= txt.length) {
                      clearInterval(intentAnimTimer);
                      intentAnimTimer = null;
                    }
                  }, 45);
                }
              } catch {}
            }
            break;

          case "tool_done":
            activeTools.value = activeTools.value.filter(
              (t) => t !== data.tool,
            );
            {
              const tc = toolCalls.find((t) => t.id === data.id);
              if (tc) {
                tc.done = true;
                tc.success = data.success;
                tc.durationMs = data.durationMs;
                tc.result = data.result || null;
                tc.error = data.error || null;
                tc.cooling = null;
              }
            }
            perSessionToolCalls.set(streamingId, [...toolCalls]);
            toolCalls = perSessionToolCalls.get(streamingId);
            if (
              data.tool === "SuggestFollowUp" &&
              data.success &&
              data.result
            ) {
              try {
                const fu = JSON.parse(data.result);
                if (fu.label && fu.prompt) streamFollowUp.value = fu;
              } catch {}
            }
            break;

          case "chart":
            charts.push(data.options);
            perSessionCharts.set(streamingId, [...charts]);
            charts = perSessionCharts.get(streamingId);
            if (isActiveView()) scrollToBottom();
            break;

          case "cooling_down": {
            // 429/5xx backoff in flight. Insert/refresh a separate ephemeral
            // ghost row in perSessionCoolers — NOT a label swap on the real
            // tool. The ghost has its own animated background and an
            // expand-to-detail panel showing the throttled URL. It auto-
            // removes after the wait elapses (or sooner if tool_done arrives).
            const list = perSessionCoolers.get(streamingId) || [];
            const key = `${data.tool || "http"}|${data.url || ""}`;
            const existing = list.find((c) => c._key === key);
            if (existing) {
              existing.attempt = data.attempt;
              existing.wait = data.waitSeconds;
              existing.status = data.status;
              existing.ts = Date.now();
              if (existing._timer) clearTimeout(existing._timer);
              existing._timer = setTimeout(() => {
                const arr = perSessionCoolers.get(streamingId) || [];
                perSessionCoolers.set(
                  streamingId,
                  arr.filter((x) => x._uid !== existing._uid),
                );
              }, (data.waitSeconds || 5) * 1000 + 500);
              perSessionCoolers.set(streamingId, [...list]);
            } else {
              const cooler = {
                _uid: `c-${Date.now()}-${crypto.randomUUID().slice(0, 4)}`,
                _key: key,
                _isCooler: true,
                tool: data.tool || "http",
                url: data.url || "",
                attempt: data.attempt,
                wait: data.waitSeconds,
                status: data.status,
                ts: Date.now(),
                expanded: false,
                done: false,
              };
              cooler._timer = setTimeout(() => {
                const arr = perSessionCoolers.get(streamingId) || [];
                perSessionCoolers.set(
                  streamingId,
                  arr.filter((x) => x._uid !== cooler._uid),
                );
              }, (data.waitSeconds || 5) * 1000 + 500);
              list.push(cooler);
              perSessionCoolers.set(streamingId, [...list]);
            }
            // Legacy label swap on the in-flight tool — kept for accessibility.
            const inFlight = [...toolCalls].reverse().find((t) => !t.done);
            if (inFlight) {
              inFlight.cooling = {
                attempt: data.attempt,
                wait: data.waitSeconds,
              };
              perSessionToolCalls.set(streamingId, [...toolCalls]);
              toolCalls = perSessionToolCalls.get(streamingId);
            }
            break;
          }

          case "html_ready":
            htmlReady.value = {
              fileId: data.fileId,
              fileName: data.fileName,
              slideCount: data.slideCount,
              createdAt: new Date().toLocaleString(undefined, {
                dateStyle: "medium",
                timeStyle: "short",
              }),
            };
            scrollToBottom();
            break;

          case "script_ready":
            scriptReady.value = {
              fileId: data.fileId,
              fileName: data.fileName,
              lineCount: data.lineCount,
              language: data.language,
              description: data.description,
              content: data.content,
              expanded: false,
            };
            scrollToBottom();
            break;

          case "maturity_score":
            try {
              const level = data.level?.toLowerCase();
              if (
                level &&
                (level === "crawl" ||
                  level === "walk" ||
                  level === "run" ||
                  level === "playbook")
              ) {
                const scores =
                  typeof data.scores === "string"
                    ? JSON.parse(data.scores)
                    : data.scores;
                if (Array.isArray(scores)) {
                  maturityScores[level] = scores;
                }
              }
            } catch {}
            break;

          case "error":
            streamBuffer.value += `\n**Error:** ${data.message}`;
            break;

          case "timing":
            // Already captured above; no UI action needed.
            break;
        }
      }
    }

    // Flush any remaining animated text before saving
    flushText();

    // Clean up final message: strip thinking lines, fix missing spaces after periods
    const clean = streamBuffer.value
      .replace(/\n*\*[A-Z][^*]{3,60}\.{3}\*\n*/g, "\n")
      .replace(/\.([A-Z])/g, ".\n\n$1")
      .replace(/^\n+/, "")
      .trim();
    const msgObj = {
      role: "assistant",
      content: clean,
      toolCalls: toolCalls.map((tc) => ({ ...tc, expanded: false })),
      charts: [...charts],
      followUp: streamFollowUp.value ? { ...streamFollowUp.value } : null,
    };
    if (htmlReady.value) {
      msgObj.html = { ...htmlReady.value };
      htmlReady.value = null;
    }
    if (scriptReady.value) {
      msgObj.script = { ...scriptReady.value };
      scriptReady.value = null;
    }
    // Only commit the assistant message into the visible message list if
    // the user is still viewing this stream's session. Otherwise the
    // server has already persisted it and the user will see it via the
    // /messages reload when they navigate back.
    if (isActiveView()) {
      console.log(
        "[push assistant] live-stream commit. content length=",
        clean.length,
        "first 80=",
        clean.slice(0, 80),
        "messages.length now=",
        messages.value.length + 1,
      );
      messages.value.push(msgObj);
    }
  } catch (err) {
    if (err.name === "AbortError") {
      if (streamBuffer.value && isActiveView()) {
        messages.value.push({
          role: "assistant",
          content: streamBuffer.value + "\n\n*(generation stopped)*",
          toolCalls: toolCalls.map((tc) => ({ ...tc, expanded: false })),
          charts: [...charts],
        });
      }
    } else if (isActiveView()) {
      messages.value.push({
        role: "assistant",
        content: `**Connection error:** ${err.message}`,
      });
    }
  } finally {
    clearInterval(intentAnimTimer);
    intentAnimTimer = null;
    runningSessions.delete(streamingId);

    // === TIMING DUMP ===
    // Stash on window for easy copy-paste; print a flat table to the
    // devtools console so we can eyeball where the time went.
    recordTiming({ kind: "client", phase: "send.end" });
    try {
      window.__lastTimings = timings;
      window.__lastTimingsPrompt = prompt;
      // eslint-disable-next-line no-console
      console.groupCollapsed(`[timing] ${prompt.slice(0, 60)} — ${timings.length} events, total ${timings[timings.length - 1]?.t_ms ?? 0}ms`);
      // eslint-disable-next-line no-console
      console.table(timings);
      // eslint-disable-next-line no-console
      console.groupEnd();
    } catch {}
    // Only wipe the live-stream UI refs if this stream's session is still
    // the foreground view. If the user navigated to a different session,
    // those refs reflect *that* session's state and must not be touched.
    // Stream is done — the persisted assistant message snapshot owns
    // toolCalls/charts now, so drop the live bucket for this session
    // unconditionally. Doing this regardless of isActiveView() keeps the
    // per-session map from accumulating stale entries when the user is
    // viewing a different tab at completion.
    perSessionToolCalls.delete(streamingId);
    perSessionCharts.delete(streamingId);
    // Discard any lingering cooling-down ghosts — they're stream-scoped only.
    const lingering = perSessionCoolers.get(streamingId) || [];
    for (const c of lingering) { if (c._timer) clearTimeout(c._timer); }
    perSessionCoolers.delete(streamingId);
    if (isActiveView()) {
      flushText();
      streamBuffer.value = "";
      activeTools.value = [];
      streamFollowUp.value = null;
      streamIntent.value = "";
      scriptReady.value = null;
      htmlReady.value = null;
      abortController = null;
      nextTick(() => inputEl.value?.focus());
    }
    // Refresh the Conversations sidebar so the new/updated summary shows up.
    loadSessions();
    if (availableModels.value.length <= 1) {
      try {
        const mr = await fetch("/api/models");
        if (mr.ok) {
          const models = await mr.json();
          if (Array.isArray(models) && models.length > 0) {
            availableModels.value = models
              .map((m) => m.id || m.name || m)
              .filter(Boolean);
          }
        }
      } catch {}
    }
  }
}
</script>

<style scoped>
/* ===== Modern, smooth scrollbars (all scrollables) ===== */
:deep(*) {
  scrollbar-width: thin;
  scrollbar-color: rgba(15, 23, 42, 0.18) transparent;
}
:deep(*::-webkit-scrollbar) {
  width: 10px;
  height: 10px;
}
:deep(*::-webkit-scrollbar-track) {
  background: transparent;
}
:deep(*::-webkit-scrollbar-thumb) {
  background: rgba(15, 23, 42, 0.18);
  border-radius: 999px;
  border: 2px solid transparent;
  background-clip: padding-box;
  transition: background 0.15s;
}
:deep(*::-webkit-scrollbar-thumb:hover) {
  background: rgba(15, 23, 42, 0.32);
  background-clip: padding-box;
  border: 2px solid transparent;
}
:deep(*::-webkit-scrollbar-corner) {
  background: transparent;
}
/*
 * ══════════════════════════════════════════════════════════
 * Design tokens (Fluent / Azure Portal)
 *
 * Text:       #323130 (primary), #605e5c (secondary)
 * Accent:     #0078d4 (hover: #106ebe)
 * Borders:    #e1dfdd
 * Hover bg:   #f3f2f1
 * Surfaces:   #fff
 * Font:       14px "Segoe UI" base, 13px body text
 * Radius:     4px (controls), 8px (cards/panels)
 * ══════════════════════════════════════════════════════════
 */

/* ── Layout shell ── */
.chat-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
}
.portal-body {
  display: flex;
  flex-direction: row;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

/* ── Azure Portal Top Bar ── */
.portal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 40px;
  background: linear-gradient(90deg, #005a9e 0%, #0078d4 55%, #0098e0 100%);
  color: #fff;
  padding: 0 12px;
  flex-shrink: 0;
  z-index: 100;
  position: relative;
}
.portal-trustline {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  font-weight: 500;
  letter-spacing: 0.03em;
  line-height: 1;
  color: rgba(255, 255, 255, 0.9);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 60vw;
}
.portal-trustline-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #ffffff;
  flex-shrink: 0;
  opacity: 0.85;
}
.portal-trustline-sep {
  opacity: 0.5;
  display: inline-flex;
  align-items: center;
  line-height: 1;
}
.portal-trustline-item {
  display: inline-flex;
  align-items: center;
  line-height: 1;
  transform: translateY(-1px);
}
.portal-trustline-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  line-height: 1;
  color: inherit;
  text-decoration: none;
  opacity: 0.95;
  transition:
    color 0.15s,
    opacity 0.15s;
}
.portal-trustline-link svg {
  display: block;
}
.portal-trustline-link:hover {
  color: #ffffff;
  opacity: 1;
}
@media (max-width: 720px) {
  .portal-trustline {
    display: none;
  }
}
.portal-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}
.portal-burger {
  background: none;
  border: none;
  color: #fff;
  cursor: pointer;
  padding: 4px;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.15s;
}
.portal-burger:hover {
  background: rgba(255, 255, 255, 0.15);
}
.portal-title {
  font-size: 14px;
  font-weight: 600;
  color: #fff;
}
.portal-readonly-badge {
  font-size: 10px;
  font-weight: 500;
  color: rgba(255, 255, 255, 0.85);
  background: rgba(255, 255, 255, 0.1);
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 3px;
  padding: 1px 6px;
  margin-left: 8px;
  letter-spacing: 0.3px;
  cursor: default;
}
.portal-header-right {
  display: flex;
  align-items: center;
  gap: 8px;
}
.portal-build-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  padding: 3px 10px;
  border-radius: 10px;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.3px;
  background: rgba(255, 255, 255, 0.12);
  color: #fff;
  white-space: nowrap;
}
.portal-build-badge--preview {
  background: #ffb900;
  color: #1f1f1f;
}
.portal-build-badge-sep {
  opacity: 0.6;
}
.portal-build-badge-branch {
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  text-transform: lowercase;
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
}
.portal-header-email {
  font-size: 14px;
  font-weight: 600;
  color: #fff;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 220px;
}
.portal-header-disconnect {
  background: none;
  border: none;
  color: rgba(255, 255, 255, 0.8);
  cursor: pointer;
  padding: 4px;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition:
    background 0.15s,
    color 0.15s;
}
.portal-header-disconnect:hover {
  background: rgba(255, 255, 255, 0.15);
  color: #fff;
}
.portal-user-identity {
  display: flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
  padding: 2px 8px;
  border-radius: 4px;
  transition: background 0.15s;
}
.portal-user-identity:hover {
  background: rgba(255, 255, 255, 0.12);
}
.portal-user-identity--anon {
  cursor: default;
}
.portal-user-identity--anon:hover {
  background: none;
}
.portal-user-text {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  line-height: 1.2;
}
.portal-user-email {
  font-size: 13px;
  font-weight: 400;
  color: #fff;
  max-width: 200px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.portal-user-tenant {
  font-size: 11px;
  font-weight: 400;
  color: rgba(255, 255, 255, 0.7);
  text-transform: uppercase;
}
.portal-user-avatar {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.2);
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

/* ── Left sidebar ── */
.sidebar {
  width: 290px;
  flex-shrink: 0;
  border-right: 1px solid #e1dfdd;
  background: #fff;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  transition:
    width 0.25s ease,
    opacity 0.2s ease;
}
.sidebar--collapsed {
  width: 0;
  opacity: 0;
  border-right: none;
  overflow: hidden;
}
.sidebar-scroll {
  flex: 1;
  overflow-y: auto;
  padding: 8px 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.sidebar-category-label {
  font-size: 14px;
  font-weight: 600;
  text-transform: none;
  letter-spacing: 0;
  color: #323130;
  margin-bottom: 4px;
  padding: 0 16px;
}
.sidebar-category--border {
  margin-top: 12px;
  padding-top: 4px;
}
.sidebar-category-label--toggle {
  display: flex;
  align-items: center;
  justify-content: space-between;
  cursor: pointer;
  user-select: none;
  border-radius: 2px;
  padding: 4px 16px;
}
.sidebar-category-label--toggle:hover {
  background: #f3f2f1;
}
.collapse-chevron {
  width: 14px;
  height: 14px;
  transition: transform 0.15s ease;
  flex-shrink: 0;
}
.collapse-chevron--collapsed {
  transform: rotate(-90deg);
}
.collapse-body {
  overflow: hidden;
  max-height: 2000px;
  opacity: 1;
  transition:
    max-height 0.3s ease,
    opacity 0.25s ease;
}
.collapse-body--collapsed {
  max-height: 0;
  opacity: 0;
  transition:
    max-height 0.25s ease,
    opacity 0.15s ease;
}
.sidebar-question {
  display: flex;
  align-items: center;
  width: calc(100% - 20px);
  margin: 1px 10px;
  padding: 8px 12px;
  border: 1px solid transparent;
  border-radius: 6px;
  background: transparent;
  font-size: 13px;
  font-weight: 400;
  color: #323130;
  cursor: pointer;
  text-align: left;
  line-height: 1.4;
  transition:
    background 0.15s ease,
    box-shadow 0.15s ease,
    color 0.15s ease;
  font-family: inherit;
}
.sidebar-question:hover:not(:disabled) {
  background: rgba(15, 23, 42, 0.04);
  color: #1a1a1a;
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.06);
}
.sidebar-question:disabled {
  opacity: 0.4;
  cursor: default;
}
.sidebar-question-label--score {
  font-weight: 600;
  font-size: 13px;
}
.sidebar-question--score-cta {
  margin: 8px 12px 10px;
  padding: 8px 12px;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  text-align: center;
  color: #0078d4;
  width: calc(100% - 24px);
  font-weight: 600;
}
.sidebar-question--score-cta:hover {
  background: rgba(0, 120, 212, 0.06);
  color: #005a9e;
  box-shadow: 0 1px 2px rgba(0, 120, 212, 0.08);
}

/* ── Maturity score cards (Crawl / Walk / Run) — clickable hero cards ── */
.maturity-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin: 8px 12px;
  padding: 14px 14px 12px;
  border: 1px solid #e1dfdd;
  border-radius: 8px;
  background: #fff;
  cursor: pointer;
  box-shadow:
    0 2px 4px rgba(15, 23, 42, 0.06),
    0 1px 2px rgba(15, 23, 42, 0.04);
  transition:
    border-color 0.2s ease,
    box-shadow 0.25s ease,
    transform 0.2s cubic-bezier(0.34, 1.56, 0.64, 1);
  user-select: none;
}
.maturity-card:hover:not(.maturity-card--disabled) {
  border-color: #c8c6c4;
  box-shadow:
    0 8px 16px rgba(15, 23, 42, 0.12),
    0 2px 4px rgba(15, 23, 42, 0.08);
  transform: translateY(-2px);
}
.maturity-card:focus,
.maturity-card:focus-visible {
  outline: none;
}
.maturity-card--disabled {
  opacity: 0.55;
  cursor: default;
}
.maturity-card--scored {
  background: #fff;
}
.maturity-card-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
}
.maturity-card-title {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.maturity-card-label {
  font-size: 14px;
  font-weight: 700;
  color: #1f2328;
  letter-spacing: 0.2px;
}
.maturity-card-subtitle {
  font-size: 11px;
  font-weight: 400;
  color: #656d76;
}
.maturity-card-cta {
  flex-shrink: 0;
  font-size: 11px;
  font-weight: 600;
  color: #8a8886;
  text-transform: uppercase;
  letter-spacing: 0.4px;
  white-space: nowrap;
}
.maturity-card:hover:not(.maturity-card--disabled) .maturity-card-cta {
  color: #1f2328;
}
.maturity-card-body {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 10px;
  margin-top: 2px;
}
.maturity-card-stars {
  font-size: 22px;
  letter-spacing: 3px;
  line-height: 1;
}
.maturity-card-chevron {
  width: 16px;
  height: 16px;
  color: #8a8886;
  cursor: pointer;
  padding: 2px;
  border-radius: 4px;
  transition:
    transform 0.15s ease,
    background 0.15s ease,
    color 0.15s ease;
}
.maturity-card-chevron:hover {
  background: rgba(15, 23, 42, 0.06);
  color: #1f2328;
}
.maturity-card-chevron:focus,
.maturity-card-chevron:focus-visible {
  outline: none;
}

.sidebar-subgroup {
  margin-top: 6px;
}
.sidebar-subgroup:first-child {
  margin-top: 0;
}
.sidebar-subgroup-label {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px 8px 20px;
  font-size: 12px;
  font-weight: 600;
  color: #605e5c;
  cursor: pointer;
  user-select: none;
}
.sidebar-subgroup-label:hover {
  background: #f3f2f1;
}
.sidebar-question--locked {
  opacity: 0.4;
}
.sidebar-question--locked:hover {
  background: transparent;
  color: #323130;
}

/* ── Category header layout ── */
.sidebar-category-label {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.sidebar-category-left {
  display: flex;
  flex-direction: column;
  gap: 1px;
}
.sidebar-category-subtitle {
  font-size: 10px;
  font-weight: 400;
  color: #a19f9d;
  letter-spacing: 0.2px;
}
.sidebar-category-right {
  display: flex;
  align-items: center;
  gap: 6px;
}
.sidebar-stars {
  font-size: 11px;
  letter-spacing: 1px;
}

/* ── Assessment summary rows ── */
.assessment-summary {
  padding: 8px 4px 4px;
  margin-top: 8px;
  background: transparent;
}
.assessment-row {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 4px 10px;
}
.assessment-label {
  color: #323130;
  font-weight: 600;
  font-size: 14px;
  line-height: 1.3;
}
.assessment-stars {
  font-size: 22px;
  letter-spacing: 3px;
  line-height: 1;
  padding: 2px 0;
}
.assessment-detail-text {
  font-size: 11px;
  color: #605e5c;
  line-height: 1.4;
}
.sidebar-source {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 5px 16px;
  font-size: 13px;
  color: #323130;
}
.sidebar-source-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
  background: #107c10;
  box-shadow: 0 0 4px rgba(16, 124, 16, 0.4);
}
.sidebar-source-dot--azure {
  background: #0078d4;
  box-shadow: 0 0 4px rgba(0, 120, 212, 0.4);
}
.sidebar-source-divider {
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: #605e5c;
  padding: 6px 16px 2px;
}
.sidebar-sub {
  display: flex;
  flex-direction: column;
  padding: 8px 16px;
  gap: 2px;
  border-bottom: 1px solid #f3f2f1;
}
.sidebar-sub:last-child {
  border-bottom: none;
}
.sidebar-sub-name {
  font-size: 13px;
  font-weight: 600;
  color: #201f1e;
  line-height: 1.3;
  word-break: break-word;
}
.sidebar-sub-id {
  font-size: 11px;
  color: #605e5c;
  font-family: "Cascadia Code", "Fira Code", Consolas, monospace;
  word-break: break-all;
  line-height: 1.35;
}
.sidebar-sub-tenant {
  font-size: 11px;
  color: #8a8886;
  line-height: 1.3;
  word-break: break-word;
}
.sidebar-footer {
  flex-shrink: 0;
  padding: 10px 14px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  background: #fff;
}

/* ── Azure connect / status (sidebar footer) ── */
.tenant-error-banner {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 4px;
  background: #fef0f1;
  border: 1px solid #f3d6d8;
  font-size: 11.5px;
  color: #a4262c;
  line-height: 1.4;
  margin-bottom: 6px;
}
.tenant-error-banner svg {
  flex-shrink: 0;
  margin-top: 1px;
}
.es-tenant-error {
  max-width: 400px;
  margin: 0 auto 8px;
}
.tenant-input-area {
  margin-bottom: 6px;
}
.tenant-input {
  width: 100%;
  padding: 6px 10px;
  border-radius: 4px;
  border: 1px solid #e1dfdd;
  font-size: 12px;
  color: #323130;
  background: #faf9f8;
  outline: none;
  box-sizing: border-box;
  transition:
    border-color 0.15s,
    box-shadow 0.15s;
}
.tenant-input::placeholder {
  color: #a19f9d;
}
.tenant-input:focus {
  border-color: #0078d4;
  background: #fff;
  box-shadow: 0 0 0 1px #0078d4;
}
.tenant-input--highlight {
  border-color: #0078d4;
  background: #fff;
  box-shadow: 0 0 0 1px #0078d4;
}
.tenant-hint {
  display: block;
  font-size: 10.5px;
  color: #605e5c;
  margin-top: 3px;
}
.es-tenant-input-row {
  width: 100%;
  max-width: 360px;
}
.es-tenant-input {
  max-width: 360px;
  text-align: center;
}
.azure-connect-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 14px;
  border-radius: 4px;
  border: 1px solid #e1dfdd;
  background: #fff;
  color: #323130;
  font: inherit;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.15s;
  justify-content: center;
}
.azure-connect-btn:hover {
  border-color: #0078d4;
  color: #0078d4;
  background: #f3f2f1;
}
.saved-tenants {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-bottom: 4px;
}
.saved-tenants-label {
  font-size: 11px;
  color: #8a8886;
  margin-right: 2px;
}
.saved-tenant-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 12px;
  border-radius: 16px;
  border: 1px solid #e1dfdd;
  background: #f3f2f1;
  color: #323130;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.15s;
}
.saved-tenant-btn:hover {
  border-color: #0078d4;
  color: #0078d4;
  background: #deecf9;
}
.azure-connect-hint {
  display: block;
  font-size: 11px;
  color: #8a8886;
  text-align: center;
  margin-top: 4px;
}
.azure-status {
  padding: 2px 0;
}
.azure-status-info {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  border-radius: 4px;
  background: #f3f2f1;
  border: 1px solid #e1dfdd;
}
.azure-status-text {
  flex: 1;
  font-size: 13px;
  color: #323130;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.azure-disconnect-btn {
  background: none;
  border: none;
  color: #605e5c;
  cursor: pointer;
  padding: 2px;
  border-radius: 4px;
  display: flex;
  align-items: center;
}
.azure-disconnect-btn:hover {
  color: #d13438;
}
/* ── Tenant switcher ── */
.tenant-switcher {
  margin-top: 6px;
}
.tenant-switch-label {
  display: flex;
  align-items: center;
  gap: 5px;
  font-size: 11.5px;
  color: #0078d4;
  cursor: pointer;
  user-select: none;
  padding: 2px 0;
}
.tenant-switch-label:hover {
  color: #106ebe;
}
.tenant-list {
  margin-top: 4px;
  max-height: 160px;
  overflow-y: auto;
  border: 1px solid #e1dfdd;
  border-radius: 4px;
  background: #faf9f8;
}
.tenant-list-item {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 6px 10px;
  border: none;
  background: transparent;
  color: #323130;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
  transition: background 0.1s;
}
.tenant-list-item:hover:not(.active) {
  background: #edebe9;
}
.tenant-list-item.active {
  background: #e6f2fb;
  cursor: default;
}
.tenant-list-item + .tenant-list-item {
  border-top: 1px solid #edebe9;
}
.tenant-list-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tenant-list-current {
  font-size: 10px;
  color: #0078d4;
  font-weight: 600;
  text-transform: uppercase;
  flex-shrink: 0;
}
.tenant-list-domain {
  font-size: 10px;
  color: #8a8886;
  flex-shrink: 0;
}
/* ── Add-on scopes (delegated, incremental consent) ── */
.addons-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 10px;
}
.addons-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 12px;
  margin-bottom: 2px;
  background: none;
  border: none;
  cursor: pointer;
  width: 100%;
  text-align: left;
  border-radius: 4px;
  transition: background 0.15s;
}
.addons-heading:hover {
  background: #f3f2f1;
}
.addons-heading-text {
  display: flex;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
}
.addons-heading-chevron {
  flex-shrink: 0;
  color: #605e5c;
  margin-left: 8px;
  transition: transform 0.4s cubic-bezier(0.4, 0, 0.2, 1);
}
.addons-heading-chevron.open {
  transform: rotate(180deg);
}
.addons-body-wrap {
  max-height: 0;
  overflow: hidden;
  opacity: 0;
  transition:
    max-height 0.6s cubic-bezier(0.4, 0, 0.2, 1),
    opacity 0.45s ease;
}
.addons-body-wrap.open {
  max-height: 1200px;
  opacity: 1;
}
.addons-body {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.addons-title {
  font-size: 11px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.4px;
  color: #605e5c;
}
.addons-sub {
  font-size: 11px;
  color: #8a8886;
  line-height: 1.35;
}
.addons-sub strong {
  color: #323130;
  font-weight: 600;
}
.scope-row {
  border-radius: 6px;
  border: 1px solid #e1dfdd;
  background: #fff;
  transition:
    border-color 0.15s,
    background 0.15s;
  overflow: hidden;
}
.scope-row--open {
  border-color: #c7c6c4;
  background: #fafafa;
}
.scope-row--active {
  background: #f3faf3;
  border-color: #cce8cc;
}
/* Onboarding glow tour — pulses each row briefly so user sees it's expandable */
.scope-row--glow {
  animation: scope-glow 0.75s ease-in-out;
}
@keyframes scope-glow {
  0% {
    box-shadow: 0 0 0 0 rgba(0, 120, 212, 0);
    border-color: #e1dfdd;
  }
  40% {
    box-shadow: 0 0 0 4px rgba(0, 120, 212, 0.25);
    border-color: #0078d4;
  }
  100% {
    box-shadow: 0 0 0 0 rgba(0, 120, 212, 0);
    border-color: #e1dfdd;
  }
}
.scope-row-summary {
  display: flex;
  align-items: stretch;
  gap: 8px;
  padding: 0 0 0 9px;
  cursor: pointer;
  width: 100%;
  background: none;
  border: none;
  text-align: left;
  font: inherit;
  color: inherit;
  transition: background 0.15s;
  min-height: 36px;
}
.scope-row-summary:hover {
  background: #f5fbff;
}
.scope-row--open .scope-row-summary:hover {
  background: #f0f0f0;
}
.scope-row-mark {
  flex-shrink: 0;
  align-self: center;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-weight: 700;
  font-size: 12px;
  background: #f3f2f1;
  color: #605e5c;
}
.scope-row--active .scope-row-mark {
  background: #107c10;
  color: #fff;
}
.scope-row-title {
  font-size: 13px;
  font-weight: 600;
  color: #201f1e;
  line-height: 1.25;
  flex: 1;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  align-self: center;
}
.scope-row-chevron {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  align-self: stretch;
  color: #8a8886;
  transition: transform 0.4s cubic-bezier(0.4, 0, 0.2, 1);
  pointer-events: none;
}
.scope-row--open .scope-row-chevron {
  transform: rotate(180deg);
}
.scope-row-detail-wrap {
  max-height: 0;
  overflow: hidden;
  transition: max-height 0.5s cubic-bezier(0.4, 0, 0.2, 1);
}
.scope-row--open .scope-row-detail-wrap {
  max-height: 280px;
}
.scope-row-detail {
  padding: 4px 9px 9px 35px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.scope-row-desc {
  font-size: 11.5px;
  color: #605e5c;
  line-height: 1.35;
  margin: 0;
}
.scope-row-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.scope-badge {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 3px;
  background: #f3f2f1;
  color: #605e5c;
  font-size: 10px;
  font-weight: 500;
  line-height: 1.5;
}
.scope-badge--delegated {
  background: #e0f2ff;
  color: #005a9e;
}
/* Compact summary-only delegated chip — just the icon */
.scope-row-summary > .scope-badge--delegated {
  padding: 1px 4px;
  font-size: 11px;
}
.scope-row-perms {
  font-family: "Cascadia Code", "Consolas", monospace;
  font-size: 10px;
  color: #8a8886;
  line-height: 1.4;
  word-break: break-word;
  margin: 0;
}
.scope-row-connect {
  align-self: flex-start;
  margin-top: 2px;
  padding: 4px 12px;
  border-radius: 4px;
  border: 1px solid #0078d4;
  background: #0078d4;
  color: #fff;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: background 0.15s;
}
.scope-row-connect:hover {
  background: #106ebe;
  border-color: #106ebe;
}
.scope-row-status {
  font-size: 11.5px;
  font-weight: 600;
  color: #107c10;
}
.scope-row-add {
  align-self: flex-start;
  margin-top: 4px;
  padding: 4px 12px;
  border-radius: 4px;
  border: 1px solid #0078d4;
  background: #0078d4;
  color: #fff;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition:
    background 0.15s,
    border-color 0.15s;
}
.scope-row-add:hover {
  background: #106ebe;
  border-color: #106ebe;
}
.addons-divider {
  height: 1px;
  background: #edebe9;
  margin: 4px 0 2px;
}
.scope-grant-all {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px 9px;
  border-radius: 6px;
  border: 1px dashed #b3b0ad;
  background: #fafafa;
  text-align: left;
  cursor: pointer;
  transition: all 0.15s;
  width: 100%;
}
.scope-grant-all:hover {
  border-color: #605e5c;
  border-style: solid;
  background: #f3f2f1;
}
.scope-grant-all-icon {
  flex-shrink: 0;
  font-size: 14px;
  margin-top: 1px;
}
.scope-grant-all-body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}
.scope-grant-all-title {
  font-size: 12.5px;
  font-weight: 600;
  color: #323130;
  line-height: 1.3;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}
.scope-grant-all-tag {
  font-size: 9.5px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.4px;
  padding: 1px 5px;
  border-radius: 3px;
  background: #fff4ce;
  color: #8a6914;
}
.scope-grant-all-desc {
  font-size: 11px;
  color: #605e5c;
  line-height: 1.35;
}
.azure-revoke-btn {
  width: 100%;
  padding: 6px 8px;
  margin-top: 4px;
  border-radius: 4px;
  border: none;
  background: transparent;
  color: #8a8886;
  font-size: 11.5px;
  cursor: pointer;
  transition: color 0.15s;
  text-decoration: underline;
  text-underline-offset: 2px;
}
.azure-revoke-btn:hover {
  color: #d13438;
}

/* ── Chat main area ── */
.chat-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  position: relative;
}

/* ── Knowledge Base (sidebar + modal) ── */
.kb-intro {
  font-size: 11px;
  line-height: 1.5;
  color: #605e5c;
  margin: 4px 2px 8px;
}
.kb-help-link {
  color: #0078d4;
  text-decoration: none;
  white-space: nowrap;
}
.kb-help-link:hover {
  text-decoration: underline;
}
.kb-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  padding: 6px 8px;
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.12s;
}
.kb-item:hover {
  background: #f3f2f1;
}
.kb-item--inactive {
  opacity: 0.55;
}
.kb-item-main {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
}
.kb-item-title {
  font-size: 12px;
  color: #323130;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.kb-item-cat {
  font-size: 10px;
  color: #797775;
}
.kb-item-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}
.kb-icon-btn {
  border: 1px solid #e1dfdd;
  background: #fff;
  color: #605e5c;
  font-size: 10.5px;
  line-height: 1;
  padding: 3px 6px;
  border-radius: 4px;
  cursor: pointer;
  transition:
    background 0.12s,
    border-color 0.12s,
    color 0.12s;
}
.kb-icon-btn:hover {
  background: #f3f2f1;
  border-color: #c8c6c4;
}
.kb-icon-btn--danger:hover {
  background: #fef0f1;
  border-color: #f3d6d8;
  color: #a4262c;
}
.kb-add-btn {
  width: 100%;
  margin-top: 6px;
  padding: 7px 10px;
  border: 1px dashed #c8c6c4;
  background: #fff;
  color: #0078d4;
  font-size: 12px;
  font-weight: 500;
  border-radius: 4px;
  cursor: pointer;
  transition:
    background 0.12s,
    border-color 0.12s;
}
.kb-add-btn:hover:not(:disabled) {
  background: #f3f9fd;
  border-color: #0078d4;
}
.kb-add-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.kb-import-btn {
  width: 100%;
  margin-top: 6px;
  padding: 6px 10px;
  border: 1px solid #e1dfdd;
  background: #fff;
  color: #605e5c;
  font-size: 11.5px;
  font-weight: 500;
  border-radius: 4px;
  cursor: pointer;
  transition:
    background 0.12s,
    border-color 0.12s;
}
.kb-import-btn:hover:not(:disabled) {
  background: #f3f2f1;
  border-color: #c8c6c4;
}
.kb-import-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Modal */
.kb-modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 200;
  background: rgba(0, 0, 0, 0.35);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 20px;
}
.kb-modal {
  width: 100%;
  max-width: 560px;
  max-height: 90vh;
  background: #fff;
  border-radius: 8px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.24);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.kb-modal-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  border-bottom: 1px solid #edebe9;
}
.kb-modal-title {
  font-size: 15px;
  font-weight: 600;
  color: #201f1e;
}
.kb-modal-close {
  border: none;
  background: transparent;
  color: #605e5c;
  font-size: 15px;
  cursor: pointer;
  line-height: 1;
  padding: 4px;
  border-radius: 4px;
}
.kb-modal-close:hover {
  background: #f3f2f1;
}
.kb-modal-body {
  padding: 16px 18px;
  overflow-y: auto;
}
.kb-field-label {
  display: block;
  font-size: 11.5px;
  font-weight: 600;
  color: #323130;
  margin: 12px 0 4px;
}
.kb-field-label:first-child {
  margin-top: 0;
}
.kb-char-count {
  font-weight: 400;
  color: #797775;
  margin-left: 6px;
}
.kb-input,
.kb-textarea {
  width: 100%;
  padding: 8px 10px;
  border: 1px solid #e1dfdd;
  border-radius: 4px;
  font-size: 13px;
  color: #323130;
  background: #faf9f8;
  outline: none;
  box-sizing: border-box;
  font-family: inherit;
  transition:
    border-color 0.15s,
    box-shadow 0.15s;
}
.kb-textarea {
  resize: vertical;
  line-height: 1.5;
  min-height: 160px;
}
.kb-input:focus,
.kb-textarea:focus {
  border-color: #0078d4;
  background: #fff;
  box-shadow: 0 0 0 1px #0078d4;
}
.kb-modal-error {
  margin: 12px 0 0;
  padding: 8px 10px;
  border-radius: 4px;
  background: #fef0f1;
  border: 1px solid #f3d6d8;
  font-size: 12px;
  color: #a4262c;
}
.kb-modal-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 18px;
  border-top: 1px solid #edebe9;
}
.kb-modal-help {
  font-size: 12px;
}
.kb-modal-foot-actions {
  display: flex;
  gap: 8px;
}
.kb-btn {
  padding: 7px 16px;
  border-radius: 4px;
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  border: 1px solid transparent;
  transition:
    background 0.12s,
    border-color 0.12s;
}
.kb-btn--ghost {
  background: #fff;
  border-color: #e1dfdd;
  color: #323130;
}
.kb-btn--ghost:hover {
  background: #f3f2f1;
}
.kb-btn--primary {
  background: #0078d4;
  color: #fff;
}
.kb-btn--primary:hover:not(:disabled) {
  background: #106ebe;
}
.kb-btn--primary:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* ── Drag-drop overlay ── */
.drop-overlay {
  position: absolute;
  inset: 0;
  z-index: 50;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(15, 23, 42, 0.55);
  backdrop-filter: blur(4px);
  pointer-events: all;
}
.drop-overlay-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.75rem;
  padding: 2rem 3rem;
  border: 2px dashed rgba(255, 255, 255, 0.65);
  border-radius: 16px;
  color: #fff;
  background: rgba(30, 41, 59, 0.85);
}
.drop-overlay-title {
  font-size: 1.1rem;
  font-weight: 600;
}
.drop-overlay-sub {
  font-size: 0.85rem;
  opacity: 0.8;
}

/* ── Attachment chips ── */
.attachment-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
  padding: 0.5rem 0.75rem 0;
}
.attachment-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.3rem 0.55rem;
  background: var(--bg-elev, #f1f5f9);
  border: 1px solid var(--border, #e2e8f0);
  border-radius: 999px;
  font-size: 0.78rem;
  color: var(--fg, #1e293b);
  max-width: 280px;
}
.attachment-chip--up {
  opacity: 0.7;
}
.attachment-chip--err {
  border-color: #f87171;
  color: #b91c1c;
  background: #fee2e2;
}
.attachment-chip-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 160px;
}
.attachment-chip-meta {
  opacity: 0.65;
  font-size: 0.72rem;
}
.attachment-chip-x {
  background: transparent;
  border: 0;
  cursor: pointer;
  font-size: 0.85rem;
  line-height: 1;
  padding: 0 0.15rem;
  color: inherit;
  opacity: 0.6;
}
.attachment-chip-x:hover {
  opacity: 1;
}

/* ── Inline Analyze CTA above input ── */
.attach-analyze-row {
  display: flex;
  align-items: center;
  justify-content: flex-start;
  gap: 0.6rem;
  padding: 0 0 0.25rem;
}
.attach-analyze-btn {
  align-self: flex-start;
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.5rem 1rem;
  background: #0078d4;
  color: #fff;
  border: 0;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 600;
  letter-spacing: 0.01em;
  cursor: pointer;
  box-shadow: 0 0 0 0 rgba(0, 120, 212, 0.55);
  animation: attach-analyze-glow 2.2s ease-in-out infinite;
  transition:
    background 0.15s,
    transform 0.05s;
}
.attach-analyze-btn:hover {
  background: #1184dc;
}
.attach-analyze-btn:active {
  transform: translateY(1px);
}
.attach-analyze-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
  animation: none;
  box-shadow: none;
}
@keyframes attach-analyze-glow {
  0%,
  100% {
    box-shadow: 0 0 0 0 rgba(0, 120, 212, 0.55);
  }
  50% {
    box-shadow: 0 0 0 8px rgba(0, 120, 212, 0);
  }
}

.messages {
  flex: 1;
  overflow-y: auto;
  min-height: 0;
  display: flex;
  flex-direction: column;
  scroll-behavior: smooth;
}
.messages-inner {
  padding: 1rem 2rem;
  display: flex;
  flex-direction: column;
  gap: 1rem;
  width: 100%;
  max-width: 100%;
  min-width: 0;
  flex: 1;
}

/* ── Empty state ── */
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 2rem 2rem 1rem;
  max-width: 720px;
  margin: 0 auto;
  width: 100%;
  animation: fadeSlideIn 0.35s ease;
}
@keyframes fadeSlideIn {
  from {
    opacity: 0;
    transform: translateY(8px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}
.es-headline {
  font-size: clamp(1.8rem, 3.5vw, 2.6rem);
  font-weight: 700;
  margin: 0 0 0.4rem;
  color: #1a1a1a;
}
.es-tagline {
  font-size: clamp(0.95rem, 1.2vw, 1.1rem);
  color: #605e5c;
  margin: 0;
  text-align: center;
}

/* ===== Modern hero (empty-state landing) ===== */
.hero {
  width: 100%;
  max-width: 880px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
  padding: clamp(1rem, 4vh, 2.5rem) 1rem;
}
.hero-eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: #0078d4;
  background: rgba(0, 120, 212, 0.08);
  padding: 6px 14px;
  border-radius: 999px;
  margin-bottom: clamp(1rem, 2.5vh, 1.6rem);
}
.hero-eyebrow-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #0078d4;
  box-shadow: 0 0 0 4px rgba(0, 120, 212, 0.18);
  animation: hero-pulse 2.4s ease-in-out infinite;
}
@keyframes hero-pulse {
  0%,
  100% {
    box-shadow: 0 0 0 4px rgba(0, 120, 212, 0.18);
  }
  50% {
    box-shadow: 0 0 0 8px rgba(0, 120, 212, 0.04);
  }
}
.hero-title {
  font-size: clamp(2.2rem, 5.5vw, 4.5rem);
  font-weight: 800;
  line-height: 1.05;
  letter-spacing: -0.035em;
  margin: 0 0 0.6rem;
  color: #111827;
}
.hero-title-accent {
  background: linear-gradient(135deg, #005a9e 0%, #0078d4 50%, #0098e0 100%);
  -webkit-background-clip: text;
  background-clip: text;
  -webkit-text-fill-color: transparent;
  color: transparent;
}
.hero-tagline {
  font-size: clamp(1rem, 1.4vw, 1.25rem);
  font-weight: 500;
  color: #4b5563;
  margin: 0.4rem 0 clamp(1.6rem, 4vh, 2.6rem);
  max-width: 640px;
  line-height: 1.5;
}
.hero-cards {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 14px;
  width: 100%;
}
.hero-card {
  background: #ffffff;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 14px;
  padding: 18px 18px 20px;
  text-align: left;
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.04);
}
.hero-card-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  background: linear-gradient(135deg, #e6f4fc 0%, #cfe9fa 100%);
  color: #0078d4;
  font-weight: 800;
  font-size: 16px;
  margin-bottom: 10px;
}
.hero-card-title {
  font-size: 14px;
  font-weight: 700;
  color: #111827;
  margin-bottom: 4px;
  letter-spacing: -0.01em;
}
.hero-card-desc {
  font-size: 12.5px;
  color: #6b7280;
  line-height: 1.45;
}
@media (max-width: 720px) {
  .hero-cards {
    grid-template-columns: 1fr;
  }
  .hero-eyebrow {
    font-size: 10.5px;
    padding: 5px 11px;
  }
}
.es-eyebrow {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: #0078d4;
  margin: 0 0 0.6rem;
  text-align: center;
}
.es-sub {
  font-size: 13px;
  color: #605e5c;
  margin: 0 0 1rem;
  line-height: 1.5;
  text-align: center;
  max-width: 480px;
}
.es-connect-bar {
  margin-bottom: 1rem;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
}
.es-connect-bar .es-step-btn {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 8px 20px;
  font-size: 13px;
  border-radius: 4px;
  border: none;
  font-weight: 600;
  cursor: pointer;
}
.es-capabilities {
  width: 100%;
  margin-bottom: 1.25rem;
}
.es-capabilities-grid {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 2px;
}
.es-cap-item {
  padding: 12px 16px;
}
.es-cap-title {
  font-size: 13px;
  font-weight: 600;
  color: #323130;
  line-height: 1.3;
  margin-bottom: 3px;
}
.es-cap-desc {
  font-size: 12px;
  color: #605e5c;
  line-height: 1.45;
}

/* ── Quick grid cards ── */
.es-quick-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 8px;
  width: 100%;
}
.es-quick-card {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid #e1dfdd;
  border-radius: 4px;
  background: #fff;
  cursor: pointer;
  font: inherit;
  font-size: 13px;
  color: #323130;
  text-align: left;
  opacity: 0;
  animation: staggerSlideIn 0.5s ease forwards;
  transition:
    border-color 0.15s,
    background 0.15s;
}
.es-quick-card:nth-child(1) {
  animation-delay: 0.5s;
}
.es-quick-card:nth-child(2) {
  animation-delay: 0.6s;
}
.es-quick-card:nth-child(3) {
  animation-delay: 0.7s;
}
.es-quick-card:nth-child(4) {
  animation-delay: 0.5s;
}
.es-quick-card:nth-child(5) {
  animation-delay: 0.6s;
}
.es-quick-card:nth-child(6) {
  animation-delay: 0.7s;
}
.es-quick-card:nth-child(7) {
  animation-delay: 0.5s;
}
.es-quick-card:nth-child(8) {
  animation-delay: 0.6s;
}
.es-quick-card:nth-child(9) {
  animation-delay: 0.7s;
}
.es-quick-card:hover {
  border-color: #0078d4;
  background: #f3f2f1;
}
.es-quick-icon {
  width: 28px;
  height: 28px;
  border-radius: 4px;
  background: #deecf9;
  color: #0078d4;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}
.es-quick-icon--teal {
  background: #d2f0e8;
  color: #008575;
}
.es-quick-icon--orange {
  background: #fed9cc;
  color: #d83b01;
}
.es-quick-icon--purple {
  background: #e8daef;
  color: #8661c5;
}
.es-quick-icon--green {
  background: #dff6dd;
  color: #107c10;
}
.es-quick-icon--pink {
  background: #f9e0f0;
  color: #b4009e;
}
.es-quick-icon--blue {
  background: #deecf9;
  color: #0078d4;
}
.es-quick-icon--indigo {
  background: #e0e4f5;
  color: #4f6bed;
}
.es-quick-icon--navy {
  background: #d6dbe4;
  color: #002050;
}
.es-quick-label {
  font-weight: 600;
  line-height: 1.3;
}

/* ── Onboarding / Steps ── */
.es-onboarding {
  width: 100%;
  max-width: 760px;
  margin: 0 0 1.25rem;
  padding: 1rem;
  border: 1px solid #e1dfdd;
  border-radius: 8px;
  background: #fff;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.06);
}
.es-steps {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}
.es-step {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  padding: 0.95rem;
  border: 1px solid #e1dfdd;
  border-radius: 8px;
  background: #fff;
}
.es-step--active {
  border-color: #0078d4;
}
.es-step--done {
  border-color: #107c10;
  background: #f1faf1;
}
.es-step--blocked {
  opacity: 0.72;
}
.es-step-badge {
  width: 30px;
  height: 30px;
  border-radius: 50%;
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: #deecf9;
  color: #0078d4;
  font-size: 13px;
  font-weight: 700;
}
.es-step--done .es-step-badge {
  background: #dff6dd;
  color: #107c10;
}
.es-step-body {
  min-width: 0;
}
.es-step-title-row {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 10px;
  align-items: center;
  margin-bottom: 0.25rem;
}
.es-step-title {
  font-size: 14px;
  font-weight: 600;
  color: #323130;
}
.es-step-state {
  padding: 2px 8px;
  border-radius: 4px;
  background: #dff6dd;
  color: #107c10;
  font-size: 11px;
  font-weight: 600;
}
.es-step-state--pending {
  background: #deecf9;
  color: #0078d4;
}
.es-step-state--blocked {
  background: #f3f2f1;
  color: #605e5c;
}
.es-step-copy {
  margin: 0 0 0.75rem;
  color: #605e5c;
  font-size: 13px;
  line-height: 1.5;
}
.es-step-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 34px;
  padding: 6px 16px;
  border: 1px solid transparent;
  border-radius: 4px;
  font: inherit;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition:
    background 0.15s,
    color 0.15s;
}
.es-step-btn:disabled {
  opacity: 0.7;
  cursor: wait;
}
.es-step-btn--github {
  background: #323130;
  color: #fff;
}
.es-step-btn--github:hover {
  background: #484644;
}
.es-step-btn--azure {
  background: #0078d4;
  color: #fff;
  width: 100%;
  max-width: 360px;
}
.es-step-btn--azure:hover {
  background: #106ebe;
}

/* ── Feature cards ── */
.es-features {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
  width: 100%;
  margin-bottom: 1rem;
}
.es-feature {
  display: flex;
  gap: 10px;
  padding: 12px 14px;
  border: 1px solid #e1dfdd;
  border-radius: 8px;
  background: #fff;
  transition: border-color 0.15s;
}
.es-feature:hover {
  border-color: #0078d4;
}
.es-feature-icon {
  flex-shrink: 0;
  width: 34px;
  height: 34px;
  border-radius: 4px;
  background: #0078d4;
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
}
.es-feature-icon--safe {
  background: #107c10;
}
.es-feature-text {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.es-feature-text strong {
  font-size: 13px;
  font-weight: 600;
  color: #323130;
}
.es-feature-text span {
  font-size: 12px;
  color: #605e5c;
  line-height: 1.4;
}

/* ── API pills ── */
.es-api-pills {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  justify-content: center;
  margin-bottom: 1rem;
}
.es-api-pill {
  padding: 3px 10px;
  border-radius: 4px;
  border: 1px solid #e1dfdd;
  font-size: 12px;
  color: #605e5c;
  background: #fff;
}

/* ── Comparison table ── */
.es-compare {
  width: 100%;
  margin-bottom: 1rem;
  border: 1px solid #e1dfdd;
  border-radius: 8px;
  overflow: hidden;
}
.es-compare-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
  line-height: 1.4;
}
.es-compare-table th {
  background: #f3f2f1;
  font-weight: 600;
  text-align: left;
  padding: 8px 10px;
  color: #323130;
  border-bottom: 1px solid #e1dfdd;
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 0.03em;
}
.es-compare-table th.es-compare-us {
  color: #0078d4;
}
.es-compare-table td {
  padding: 6px 10px;
  border-bottom: 1px solid #f3f2f1;
  color: #605e5c;
}
.es-compare-table td:first-child {
  font-weight: 600;
  color: #323130;
  min-width: 120px;
}
.es-compare-table td.es-compare-us {
  color: #323130;
}
.es-compare-table tr:last-child td {
  border-bottom: none;
}
.es-compare-table tr:hover td {
  background: #f3f2f1;
}

/* ── Prompt pills ── */
.es-prompts {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  justify-content: center;
  margin-bottom: 1rem;
}
.es-prompt {
  padding: 6px 14px;
  border: 1px solid #e1dfdd;
  border-radius: 4px;
  background: transparent;
  color: #323130;
  font-size: 13px;
  cursor: pointer;
  transition:
    border-color 0.15s,
    color 0.15s;
}
.es-prompt:hover {
  border-color: #0078d4;
  color: #0078d4;
}

/* ── Messages ── */
.message-row {
  display: flex;
  width: 100%;
  min-width: 0;
  max-width: 100%;
  animation: messageSlideIn 0.3s ease;
}
@keyframes messageSlideIn {
  from {
    opacity: 0;
    transform: translateY(8px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}
.message-row--user {
  justify-content: flex-end;
}
.message-row--ai {
  justify-content: flex-start;
}
.bubble--user {
  max-width: 80%;
  border-radius: 8px;
  padding: 10px 14px;
  background: #f3f2f1;
  color: #323130;
  font-size: 14px;
  line-height: 1.5;
  word-wrap: break-word;
}
.ai-row {
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-width: 100%;
  width: 100%;
  min-width: 0;
}
.ai-avatar {
  flex-shrink: 0;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  background: #323130;
  color: #fff;
  font-size: 11px;
  font-weight: 700;
  display: flex;
  align-items: center;
  justify-content: center;
}
.agent-lottie {
  flex-shrink: 0;
  width: 36px;
  height: 36px;
}
.ai-header {
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 8px;
  min-height: 28px;
}
.ai-content {
  min-width: 0;
  max-width: 100%;
  overflow-x: auto;
}
.message-text {
  font-size: 14px;
  line-height: 1.6;
  word-wrap: break-word;
  color: #323130;
}
.streaming-cursor {
  display: inline-block;
  width: 6px;
  height: 16px;
  background: #605e5c;
  border-radius: 1px;
  margin-left: 2px;
  vertical-align: text-bottom;
  animation: cursor-pulse 1s ease-in-out infinite;
}
.stream-intent {
  font-style: italic;
  color: #605e5c;
  font-size: 14px;
  font-weight: 500;
  white-space: normal;
  line-height: 1.4;
  animation: intent-in 0.3s ease-out;
}
@keyframes intent-in {
  from {
    opacity: 0;
    transform: translateX(-8px);
  }
  to {
    opacity: 1;
    transform: translateX(0);
  }
}
@keyframes cursor-pulse {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0;
  }
}

/* ── Message content styling ── */
.message-text :deep(pre) {
  background: #f3f2f1;
  border: 1px solid #e1dfdd;
  border-radius: 4px;
  padding: 12px 16px;
  margin: 8px 0;
  overflow-x: auto;
  font-size: 13px;
}
.message-text :deep(code) {
  background: rgba(0, 0, 0, 0.06);
  padding: 2px 6px;
  border-radius: 3px;
  font-size: 0.9em;
  font-family: "Cascadia Code", "Fira Code", Consolas, monospace;
}
.message-text :deep(pre code) {
  background: none;
  padding: 0;
}
.message-text :deep(.wt-wrap) {
  margin: 12px 0;
  border-radius: 12px;
  background: #ffffff;
  border: 1px solid #e1dfdd;
  box-shadow:
    0 4px 16px rgba(0, 120, 212, 0.06),
    0 1px 3px rgba(0, 0, 0, 0.04);
  overflow: hidden;
}
.message-text :deep(.wow-table) {
  border-collapse: collapse;
  width: 100%;
  font-size: 13px;
  color: #1f2328;
  background: transparent;
  display: table;
  overflow-x: auto;
}
.message-text :deep(.wow-table th) {
  text-align: left;
  padding: 11px 14px;
  color: #656d76;
  font-weight: 600;
  font-size: 10.5px;
  letter-spacing: 1.2px;
  text-transform: uppercase;
  border-bottom: 1px solid #e1dfdd;
  background: #f6f8fa;
  white-space: nowrap;
}
.message-text :deep(.wow-table td) {
  padding: 10px 14px;
  border: none;
  border-bottom: 1px solid #f0f2f5;
  color: #1f2328;
  vertical-align: middle;
}
.message-text :deep(.wow-table tbody tr:last-child td) {
  border-bottom: none;
}
.message-text :deep(.wow-table tbody tr) {
  transition: background 0.18s ease;
}
.message-text :deep(.wow-table tbody tr:hover) {
  background: rgba(0, 120, 212, 0.05);
}
.message-text :deep(.wow-table .wt-num) {
  text-align: right;
  font-family: "SF Mono", "JetBrains Mono", Menlo, Consolas, monospace;
  font-variant-numeric: tabular-nums;
  color: #1f2328;
}
.message-text :deep(.wow-table .wt-up) {
  color: #107c10;
  font-weight: 600;
}
.message-text :deep(.wow-table .wt-down) {
  color: #d83b01;
  font-weight: 600;
}
.message-text :deep(.wow-table .wt-flat) {
  color: #8b96a0;
  font-weight: 600;
}
.message-text :deep(.wow-table .wt-tag) {
  display: inline-block;
  padding: 3px 9px;
  border-radius: 12px;
  font-size: 10.5px;
  letter-spacing: 0.4px;
  text-transform: uppercase;
  font-weight: 600;
}
.message-text :deep(.wow-table .wt-tag-g) {
  background: rgba(16, 124, 16, 0.1);
  color: #107c10;
}
.message-text :deep(.wow-table .wt-tag-w) {
  background: rgba(255, 185, 0, 0.15);
  color: #b08000;
}
.message-text :deep(.wow-table .wt-tag-b) {
  background: rgba(216, 59, 1, 0.1);
  color: #d83b01;
}
.message-text :deep(.wow-table .wt-bar) {
  display: inline-block;
  position: relative;
  width: 56px;
  height: 4px;
  margin-left: 10px;
  border-radius: 2px;
  background: #eef0f3;
  vertical-align: middle;
  overflow: hidden;
}
.message-text :deep(.wow-table .wt-bar > i) {
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  background: linear-gradient(90deg, #50a8f5, #0078d4);
  border-radius: 2px;
  animation: wt-bar-grow 1s cubic-bezier(0.2, 0.8, 0.2, 1) both;
}
@keyframes wt-bar-grow {
  from {
    width: 0 !important;
  }
}

.message-text :deep(table:not(.wow-table)) {
  border-collapse: collapse;
  width: 100%;
  margin: 8px 0;
  font-size: 13px;
  display: block;
  overflow-x: auto;
}
.message-text :deep(table:not(.wow-table) th),
.message-text :deep(table:not(.wow-table) td) {
  border: 1px solid #e1dfdd;
  padding: 6px 10px;
  text-align: left;
}
.message-text :deep(table:not(.wow-table) th) {
  background: #f3f2f1;
  font-weight: 600;
  font-size: 12px;
}
.message-text :deep(table:not(.wow-table) tr:nth-child(even)) {
  background: #faf9f8;
}
.message-text :deep(h2),
.message-text :deep(h3),
.message-text :deep(h4) {
  margin: 12px 0 4px;
  font-weight: 600;
  color: #323130;
}
.message-text :deep(h2) {
  font-size: 18px;
}
.message-text :deep(h3) {
  font-size: 16px;
}
.message-text :deep(h4) {
  font-size: 14px;
}
.message-text :deep(ul) {
  margin: 4px 0;
  padding-left: 1.5rem;
}
.message-text :deep(li) {
  margin: 2px 0;
}

/* ── Charts ── */
.chart-container {
  width: 100%;
  height: 340px;
  margin: 0 0 12px;
  border-radius: 12px;
  background: #ffffff;
  border: 1px solid #e1dfdd;
  box-shadow:
    0 4px 16px rgba(0, 120, 212, 0.06),
    0 1px 3px rgba(0, 0, 0, 0.04);
  overflow: hidden;
  padding: 12px;
  transition:
    box-shadow 0.25s ease,
    transform 0.25s ease;
}
.chart-container:hover {
  box-shadow:
    0 8px 28px rgba(0, 120, 212, 0.14),
    0 2px 8px rgba(0, 0, 0, 0.06);
}

/* ── Input area ── */
.input-area {
  flex-shrink: 0;
  padding: 12px 16px;
  max-width: 900px;
  margin: 0 auto;
  width: 100%;
  padding-bottom: max(12px, env(safe-area-inset-bottom));
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 6px;
}
.input-wrapper {
  display: flex;
  flex-direction: column;
  border: 1px solid #8a8886;
  border-radius: 20px;
  padding: 14px 18px 10px 18px;
  background: #fff;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.08);
  transition:
    border-color 0.15s,
    box-shadow 0.15s;
  flex: 1;
  min-width: 0;
}
.input-wrapper:focus-within {
  border-color: #605e5c;
  box-shadow: 0 0 0 2px rgba(0, 0, 0, 0.04);
}
.input-wrapper--disabled {
  background: #f3f2f1;
  opacity: 0.6;
}
.input-field {
  flex: 1;
  background: transparent;
  border: none;
  color: #323130;
  font-size: 15px;
  font-family: inherit;
  padding: 0;
  outline: none;
  line-height: 1.5;
  resize: none;
  overflow-y: hidden;
  max-height: 400px;
  min-height: 24px;
}
.input-field::placeholder {
  color: #a19f9d;
}
.input-bottom-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 8px;
}
.input-bottom-left {
  display: flex;
  align-items: center;
  gap: 4px;
}
.input-action-btn {
  display: flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 10px;
  border-radius: 14px;
  border: none;
  background: transparent;
  color: #605e5c;
  cursor: pointer;
  font-family: inherit;
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  transition:
    color 0.15s,
    background 0.15s;
}
.input-action-btn:hover:not(:disabled) {
  background: #f3f2f1;
  color: #0078d4;
}
.input-action-btn:disabled {
  opacity: 0.35;
  cursor: default;
}
.input-bottom-right {
  display: flex;
  align-items: center;
}
.action-btn {
  flex-shrink: 0;
  width: 32px;
  height: 32px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 50%;
  border: none;
  cursor: pointer;
  transition: all 0.15s;
}
.action-btn--active {
  background: #0078d4;
  color: #fff;
}
.action-btn--active:hover {
  background: #106ebe;
}
.action-btn--disabled {
  background: #f3f2f1;
  color: #a19f9d;
  cursor: default;
}
.action-btn--stop {
  background: #323130;
  color: #fff;
}
.action-btn--stop:hover {
  background: #484644;
}

/* ── Tools sidebar (right) ── */
.tools-sidebar {
  width: 0;
  flex-shrink: 0;
  border-left: 1px solid #e1dfdd;
  background: #fff;
  overflow: hidden;
  transition: width 0.25s ease;
  display: flex;
  flex-direction: column;
  font-size: 13px;
}
.tools-sidebar--open {
  width: 250px;
}
.tools-sidebar-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 16px;
}
.tools-sidebar-header-text {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.tools-sidebar-title {
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.02em;
  color: #1a1a1a;
}
.tools-sidebar-status {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 10.5px;
  font-weight: 500;
  color: #605e5c;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.tools-sidebar-status-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tools-sidebar-status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #d0d0d0;
  flex-shrink: 0;
}
.tools-sidebar-status-dot--live {
  background: #1a7f37;
  box-shadow: 0 0 0 0 rgba(26, 127, 55, 0.5);
  animation: agent-pulse 1.4s ease-out infinite;
}
@keyframes agent-pulse {
  0% {
    box-shadow: 0 0 0 0 rgba(26, 127, 55, 0.45);
  }
  70% {
    box-shadow: 0 0 0 6px rgba(26, 127, 55, 0);
  }
  100% {
    box-shadow: 0 0 0 0 rgba(26, 127, 55, 0);
  }
}
.tools-sidebar-scroll {
  flex: 1;
  overflow-y: auto;
  padding: 4px 8px;
  scrollbar-width: thin;
}
/* ── Right sidebar split: Agent (top) + Conversations (bottom) ── */
.tools-sidebar-pane {
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.tools-sidebar-pane--agent {
  flex: 1 1 50%;
  min-height: 120px;
}
.tools-sidebar-pane--sessions {
  flex: 1 1 50%;
  min-height: 140px;
  border-top: 1px solid #e1dfdd;
  background: #fff;
}
.sessions-header {
  background: #fff;
}
.sessions-new-btn {
  font-size: 13px;
  font-weight: 600;
  padding: 6px 14px;
  border-radius: 6px;
  border: 1px solid #e1dfdd;
  background: #fff;
  color: #323130;
  cursor: pointer;
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.06);
  transition:
    background 0.15s,
    border-color 0.15s,
    box-shadow 0.15s;
}
.sessions-new-btn:hover:not(:disabled) {
  background: #fff;
  border-color: #d2d0ce;
  box-shadow: 0 1px 3px rgba(15, 23, 42, 0.08);
}
.sessions-new-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.sessions-scroll {
  padding: 4px 8px 8px;
}
.sessions-empty {
  padding: 12px 8px;
  font-size: 11.5px;
  color: #8a8886;
  font-style: italic;
  text-align: center;
}
.session-row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: calc(100% - 8px);
  margin: 0 4px;
  padding: 4px 8px;
  border: 1px solid transparent;
  border-radius: 6px;
  background: transparent;
  cursor: pointer;
  transition:
    background 0.15s ease,
    box-shadow 0.15s ease,
    color 0.15s ease;
  position: relative;
}
.session-row:hover {
  background: rgba(15, 23, 42, 0.04);
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.06);
}
.session-row--current {
  background: #f3f2f1;
}
.session-row--current:hover {
  background: #f3f2f1;
}
.session-row-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
}
.session-row-title {
  font-size: 13px;
  font-weight: 400;
  color: #323130;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  line-height: 1.4;
}
.session-row-time {
  font-size: 10.5px;
  color: #8a8886;
}
.session-row-delete {
  flex-shrink: 0;
  width: 20px;
  height: 20px;
  border: none;
  background: transparent;
  border-radius: 4px;
  font-size: 16px;
  line-height: 1;
  color: #a19f9d;
  cursor: pointer;
  opacity: 0;
  transition:
    opacity 0.12s,
    background 0.12s,
    color 0.12s;
}
.session-row:hover .session-row-delete {
  opacity: 1;
}
.session-row-delete:hover {
  background: #fde7e9;
  color: #a4262c;
}
.st-count {
  font-size: 11px;
  font-weight: 600;
  background: #f3f2f1;
  color: #605e5c;
  border-radius: 4px;
  padding: 1px 7px;
  min-width: 20px;
  text-align: center;
}
.st-icon {
  width: 15px;
  height: 15px;
  flex-shrink: 0;
}
.st-icon--spin {
  animation: icon-spin 0.9s linear infinite;
}
@keyframes icon-spin {
  to {
    transform: rotate(360deg);
  }
}
.st-icon--ok {
  animation: icon-pop 0.3s ease-out;
}
@keyframes icon-pop {
  0% {
    transform: scale(0);
    opacity: 0;
  }
  60% {
    transform: scale(1.2);
  }
  100% {
    transform: scale(1);
    opacity: 1;
  }
}
.st-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 16px;
  border-radius: 0;
  cursor: default;
  user-select: none;
  transition: background 0.1s;
  min-height: 28px;
}
.st-row:hover {
  background: #f3f2f1;
}
.st-row--clickable {
  cursor: pointer;
}
.st-row--running {
  background: #fff4ce;
}
.st-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: #323130;
  font-weight: 400;
  font-size: 13px;
}
.st-time {
  flex-shrink: 0;
  color: #605e5c;
  font-size: 11px;
}

/* ── Cooling-down ghost row (ephemeral, 429/5xx) ── */
/* Same row chrome as .st-row (size, padding, font weight 400, height);
   only the background + text colour differ so the user can spot retries.
   Gradient mirrors the Azure top bar (#005a9e → #0078d4 → #0098e0) and
   sweeps horizontally — never goes to white so dark blues stay readable. */
.st-row--cooler {
  cursor: pointer;
  position: relative;
  flex-wrap: wrap;
  background: linear-gradient(
    90deg,
    #005a9e 0%,
    #0078d4 25%,
    #0098e0 50%,
    #0078d4 75%,
    #005a9e 100%
  );
  background-size: 200% 100%;
  animation: cool-sweep 2.4s linear infinite;
}
.st-row--cooler:hover {
  filter: brightness(1.05);
}
.st-row--cooler .st-name,
.st-row--cooler .st-time {
  color: #fff;
}
.st-icon--cooler {
  width: 14px;
  height: 14px;
  color: #fff;
}
.st-cooler-detail {
  flex-basis: 100%;
  margin-top: 6px;
  padding: 8px 10px;
  background: rgba(0, 0, 0, 0.25);
  border-radius: 4px;
  color: #fff;
  font-size: 11px;
  line-height: 1.5;
  cursor: default;
}
.st-cooler-row {
  margin-bottom: 2px;
}
.st-cooler-row strong {
  color: #fff;
  font-weight: 600;
  margin-right: 4px;
}
.st-cooler-url code {
  background: rgba(0, 0, 0, 0.3);
  padding: 2px 4px;
  border-radius: 3px;
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  font-size: 10px;
  word-break: break-all;
  display: inline-block;
  max-width: 100%;
}
@keyframes cool-sweep {
  0%   { background-position: 0% 50%; }
  100% { background-position: 200% 50%; }
}

/* ── Tool popover ── */
.tool-popover {
  position: fixed;
  z-index: 9999;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  width: 800px;
  max-width: 90vw;
  max-height: 85vh;
  background: #fff;
  border: 1px solid #e1dfdd;
  border-radius: 8px;
  box-shadow: 0 8px 30px rgba(0, 0, 0, 0.12);
  overflow: hidden;
  display: flex;
  flex-direction: column;
  animation: popover-in 0.15s ease-out;
  font-family: "Cascadia Code", "Fira Code", Consolas, monospace;
}
@keyframes popover-in {
  from {
    opacity: 0;
    transform: translate(-50%, -50%) scale(0.95);
  }
  to {
    opacity: 1;
    transform: translate(-50%, -50%) scale(1);
  }
}
.tool-popover-header {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 20px;
  border-bottom: 1px solid #e1dfdd;
  background: #f3f2f1;
}
.tool-popover-name {
  font-size: 16px;
  font-weight: 600;
  color: #323130;
  flex: 1;
}
.tool-popover-time {
  font-size: 14px;
  color: #605e5c;
}
.tool-popover-close {
  background: none;
  border: none;
  font-size: 20px;
  color: #605e5c;
  cursor: pointer;
  padding: 0 4px;
  line-height: 1;
}
.tool-popover-close:hover {
  color: #323130;
}
.tool-popover-section {
  padding: 12px 20px;
  border-bottom: 1px solid #f3f2f1;
}
.tool-popover-section:last-child {
  border-bottom: none;
}
.tool-popover-body {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  scrollbar-width: thin;
}
.tool-popover-label {
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.06em;
  color: #605e5c;
  margin-bottom: 6px;
  text-transform: uppercase;
}
.tool-popover-pre {
  background: #0d1117;
  border: 1px solid #21262d;
  border-radius: 6px;
  padding: 12px 14px;
  margin: 0;
  font-family:
    ui-monospace, "SF Mono", "JetBrains Mono", "Fira Code", "Cascadia Code",
    Menlo, Consolas, "Courier New", monospace;
  font-size: 12px;
  font-variant-ligatures: none;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 500px;
  overflow-y: auto;
  line-height: 1.55;
  color: #e6edf3;
  scrollbar-width: thin;
  -webkit-font-smoothing: antialiased;
}
.tool-popover-pre code {
  background: transparent;
  padding: 0;
  font: inherit;
  color: inherit;
  display: block;
}
/* Force white-dominant scheme regardless of hljs theme */
.tool-popover-pre :deep(.hljs),
.tool-popover-pre :deep(.hljs-attr),
.tool-popover-pre :deep(.hljs-string),
.tool-popover-pre :deep(.hljs-number),
.tool-popover-pre :deep(.hljs-literal),
.tool-popover-pre :deep(.hljs-keyword),
.tool-popover-pre :deep(.hljs-punctuation),
.tool-popover-pre :deep(.hljs-built_in) {
  color: #ffffff !important;
  background: transparent !important;
}
/* Subtle accent to keep keys/strings distinguishable */
.tool-popover-pre :deep(.hljs-attr) {
  color: #79c0ff !important;
}
.tool-popover-pre :deep(.hljs-string) {
  color: #d2e4ff !important;
}
.tool-popover-pre :deep(.hljs-number),
.tool-popover-pre :deep(.hljs-literal) {
  color: #f5b483 !important;
}
.tool-popover-pre--error {
  color: #ffa198;
  background: #2d1418;
  border-color: #f85149;
}

/* ── Auth buttons ── */
.login-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 9px 14px;
  border-radius: 4px;
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.15s;
  justify-content: center;
  border: none;
}
.login-btn--github {
  background: #323130;
  color: #fff;
}
.login-btn--github:hover:not(:disabled) {
  background: #484644;
}
.login-btn:disabled,
.azure-connect-btn:disabled {
  opacity: 0.7;
  cursor: wait;
}
.auth-spinner {
  width: 16px;
  height: 16px;
  border: 2px solid rgba(255, 255, 255, 0.3);
  border-top-color: #fff;
  border-radius: 50%;
  animation: auth-spin 0.6s linear infinite;
  flex-shrink: 0;
}
.auth-spinner--azure {
  border-color: rgba(0, 120, 212, 0.2);
  border-top-color: #0078d4;
}
@keyframes auth-spin {
  to {
    transform: rotate(360deg);
  }
}

/* ── Auth overlay ── */
.auth-overlay {
  position: fixed;
  inset: 0;
  z-index: 9999;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(255, 255, 255, 0.85);
  backdrop-filter: blur(4px);
  animation: auth-overlay-in 0.2s ease;
}
@keyframes auth-overlay-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}
.auth-overlay-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 16px;
  padding: 40px 48px;
  border-radius: 8px;
  background: #fff;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.1);
}
.auth-overlay-spinner {
  width: 40px;
  height: 40px;
  border: 3px solid rgba(0, 120, 212, 0.15);
  border-top-color: #0078d4;
  border-radius: 50%;
  animation: auth-spin 0.7s linear infinite;
}
.auth-overlay-text {
  font-size: 16px;
  font-weight: 600;
  color: #323130;
  margin: 0;
}
.auth-overlay-sub {
  font-size: 13px;
  color: #605e5c;
  margin: 0;
}

/* ── Misc sidebar elements ── */
.sidebar-linkedin {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 7px;
  padding: 7px 10px;
  border-radius: 4px;
  border: 1px solid #0078d4;
  color: #0078d4;
  font-size: 13px;
  font-weight: 600;
  text-decoration: none;
  transition: background 0.15s;
}
.sidebar-linkedin:hover {
  background: #f3f2f1;
}
.new-chat-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 7px 12px;
  border-radius: 4px;
  border: 1px solid #e1dfdd;
  background: #fff;
  color: #323130;
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.15s;
}
.new-chat-btn:hover {
  background: #f3f2f1;
  border-color: #0078d4;
  color: #0078d4;
}
.sidebar-user {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 4px;
}
.sidebar-user-avatar {
  width: 28px;
  height: 28px;
  border-radius: 50%;
  flex-shrink: 0;
}
.sidebar-user-info {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
  line-height: 1.2;
}
.sidebar-user-name {
  font-size: 13px;
  font-weight: 500;
  color: #323130;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.sidebar-user-login {
  font-size: 11px;
  color: #605e5c;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.sidebar-logout-btn {
  flex-shrink: 0;
  background: transparent;
  border: none;
  color: #605e5c;
  cursor: pointer;
  padding: 4px;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.15s;
}
.sidebar-logout-btn:hover {
  color: #d13438;
  background: #fde7e9;
}
.model-selector {
  padding: 4px 0 8px;
  border-bottom: 1px solid #e1dfdd;
  margin-bottom: 4px;
}
.model-selector-label {
  display: block;
  font-size: 11px;
  font-weight: 600;
  color: #605e5c;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  margin-bottom: 4px;
}
.model-selector-select {
  width: 100%;
  padding: 5px 8px;
  font-size: 13px;
  border: 1px solid #e1dfdd;
  border-radius: 4px;
  background: #fff;
  color: #323130;
  cursor: pointer;
  outline: none;
}

/* ── HTML deck — compact download card ── */
.html-deck-card {
  margin-top: 12px;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  border: 1px solid #e1dfdd;
  border-left: 4px solid #0078d4;
  border-radius: 6px;
  background: #fafafa;
  width: 100%;
  max-width: 560px;
}
.html-deck-card-icon {
  flex-shrink: 0;
  width: 36px;
  height: 36px;
  border-radius: 8px;
  background: #deecf9;
  color: #0078d4;
  display: flex;
  align-items: center;
  justify-content: center;
}
.html-deck-card-icon svg {
  width: 20px;
  height: 20px;
}
.html-deck-card-body {
  flex: 1;
  min-width: 0;
}
.html-deck-card-title {
  font-size: 13.5px;
  font-weight: 600;
  color: #323130;
  line-height: 1.2;
}
.html-deck-card-meta {
  margin-top: 2px;
  font-size: 11.5px;
  color: #605e5c;
}
.html-deck-card-btn {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  padding: 7px 16px;
  border-radius: 4px;
  background: #0078d4;
  color: #fff;
  font-size: 13px;
  font-weight: 500;
  text-decoration: none;
  border: none;
  transition: background 0.15s;
}
.html-deck-card-btn:hover {
  background: #106ebe;
}

/* ── Script inline block ── */
.script-download-bar {
  display: flex;
  justify-content: center;
  padding: 0 1rem 8px;
  max-width: 800px;
  margin: 0 auto;
  width: 100%;
}
.script-inline-block {
  margin-top: 12px;
  border: 1px solid #e1dfdd;
  border-radius: 6px;
  overflow: hidden;
  background: #fafafa;
  width: 100%;
}
.script-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  background: #f3f2f1;
  border-bottom: 1px solid #e1dfdd;
  gap: 8px;
}
.script-header-left {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.script-filename {
  font-size: 13px;
  font-weight: 600;
  color: #323130;
  white-space: nowrap;
}
.script-meta {
  font-size: 12px;
  color: #605e5c;
  white-space: nowrap;
}
.script-header-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}
.script-copy-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 4px;
  background: transparent;
  border: 1px solid #e1dfdd;
  color: #605e5c;
  cursor: pointer;
  transition:
    background 0.15s,
    color 0.15s;
}
.script-copy-btn:hover {
  background: #deecf9;
  color: #0078d4;
}
.script-download-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 12px;
  border-radius: 4px;
  background: #0078d4;
  color: #fff;
  font-size: 12px;
  font-weight: 500;
  text-decoration: none;
  cursor: pointer;
  transition: background 0.15s;
  border: none;
  white-space: nowrap;
}
.script-download-btn:hover {
  background: #106ebe;
}
.script-description {
  padding: 6px 12px;
  font-size: 12px;
  color: #605e5c;
  background: #f9f9f9;
  border-bottom: 1px solid #e1dfdd;
}
.script-toggle-btn {
  display: block;
  width: 100%;
  padding: 8px 12px;
  background: #f3f2f1;
  color: #323130;
  border: none;
  border-bottom: 1px solid #e1dfdd;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  text-align: left;
  transition: background 0.15s;
}
.script-toggle-btn:hover {
  background: #edebe9;
}
.script-code {
  margin: 0;
  padding: 12px 14px;
  background: #1e1e1e;
  color: #d4d4d4;
  font-size: 12px;
  font-family: "Cascadia Code", "Fira Code", "Consolas", monospace;
  line-height: 1.5;
  overflow-x: auto;
  max-height: 350px;
  overflow-y: auto;
  white-space: pre;
}
.script-code code {
  font-family: inherit;
  font-size: inherit;
}

/* ── Follow-up buttons ── */
.follow-up-buttons {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 12px;
}
.follow-up-btn {
  background: #fff;
  color: #323130;
  border: 1px solid #e1dfdd;
  border-radius: 8px;
  padding: 8px 14px;
  font-size: 13px;
  cursor: pointer;
  line-height: 1.4;
  text-align: left;
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.06);
  transition:
    border-color 0.15s ease,
    box-shadow 0.15s ease,
    transform 0.15s ease;
}
.follow-up-btn:hover {
  background: #fff;
  border-color: #d2d0ce;
  box-shadow: 0 1px 3px rgba(15, 23, 42, 0.08);
  transform: translateY(-1px);
}

/* ── Mobile ── */
.mobile-auth-bar {
  display: none;
  align-items: center;
  gap: 8px;
  padding: 0 12px 4px;
  flex-wrap: wrap;
  justify-content: center;
}
.mobile-auth-btn {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 10px 22px;
  border-radius: 4px;
  font-size: 14px;
  font-weight: 600;
  font-family: inherit;
  cursor: pointer;
  transition: all 0.15s;
  border: none;
  min-height: 40px;
}
.mobile-auth-btn--github {
  background: #323130;
  color: #fff;
}
.mobile-auth-btn--github:hover:not(:disabled) {
  background: #484644;
}
.mobile-auth-btn--azure {
  background: #fff;
  color: #323130;
  border: 1px solid #e1dfdd;
}
.mobile-auth-btn--azure:hover:not(:disabled) {
  border-color: #0078d4;
  color: #0078d4;
}
.mobile-auth-btn:disabled {
  opacity: 0.7;
  cursor: wait;
}
.mobile-auth-item {
  display: flex;
  align-items: center;
}
.mobile-azure-status {
  gap: 5px;
  padding: 4px 10px;
  border-radius: 4px;
  background: #f3f2f1;
  border: 1px solid #e1dfdd;
  font-size: 12px;
  color: #323130;
}
.mobile-azure-email {
  max-width: 100px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.mobile-user-info {
  gap: 6px;
  padding: 4px 8px;
  border-radius: 4px;
  background: #fff;
  border: 1px solid #e1dfdd;
}
.mobile-user-avatar {
  width: 20px;
  height: 20px;
  border-radius: 50%;
}
.mobile-user-name {
  font-size: 12px;
  font-weight: 500;
  color: #323130;
  max-width: 80px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.es-tagline-mobile {
  display: none;
  font-size: 13px;
  color: #605e5c;
  margin: 8px 16px 0;
  padding: 0 16px;
  text-align: center;
  line-height: 1.5;
}

/* ── Responsive ── */
@media (max-width: 768px) {
  .sidebar {
    position: fixed;
    top: 48px;
    left: 0;
    bottom: 0;
    width: 80vw;
    max-width: 320px;
    z-index: 150;
    background: #fff;
    box-shadow: 2px 0 12px rgba(0, 0, 0, 0.18);
    transform: translateX(0);
    transition: transform 0.2s ease;
  }
  .sidebar--collapsed {
    transform: translateX(-100%);
    box-shadow: none;
  }
  .tools-sidebar {
    display: none;
  }
  .tool-popover {
    display: none;
  }
  .portal-user-text {
    display: none;
  }
  .es-sub {
    font-size: 12px;
  }
  .es-tagline-mobile {
    display: none;
  }
  .es-headline {
    font-size: clamp(2rem, 11vw, 3rem);
  }
  .es-quick-grid {
    grid-template-columns: 1fr 1fr;
  }
  .es-diff-grid {
    grid-template-columns: 1fr;
  }
  .empty-state {
    padding: 1rem;
    justify-content: center;
    flex: 1;
  }
  .messages-inner {
    padding: 12px;
    flex: 1;
    display: flex;
    flex-direction: column;
  }
  .input-area {
    padding: 8px 12px;
  }
  .input-pill-label {
    display: none;
  }
  .input-pill-btn {
    padding: 0;
    width: 32px;
    border: none;
    background: transparent;
  }
}
@media (max-width: 600px) {
  .es-features {
    grid-template-columns: 1fr;
  }
  .es-headline {
    font-size: clamp(1.8rem, 12vw, 2.6rem);
  }
}
@media (max-width: 768px) {
  .mobile-auth-bar {
    display: flex !important;
  }
  .es-tagline-mobile {
    display: block !important;
  }
}
</style>
