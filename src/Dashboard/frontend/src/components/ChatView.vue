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
                  'collapse-body--collapsed':
                    collapsedSections['cm_' + cat.key],
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
                msg.role === 'user'
                  ? 'message-row--user'
                  : msg.role === 'system'
                    ? 'message-row--system'
                    : 'message-row--ai'
              "
            >
              <div v-if="msg.role === 'user'" class="bubble bubble--user">
                {{ msg.content }}
              </div>
              <!-- System notices (resume / busy / recovery) get a distinct muted
                   pill instead of masquerading as AI answers with an avatar. -->
              <div v-else-if="msg.role === 'system'" class="system-notice">
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
                        {{ htmlCardMeta(msg.html.slideCount)
                        }}<span v-if="msg.html.createdAt">
                          · {{ msg.html.createdAt }}</span
                        >
                      </div>
                    </div>
                    <a
                      v-if="!msg.html.expired"
                      :href="'/api/download/html/' + msg.html.fileId"
                      :download="msg.html.fileName"
                      class="html-deck-card-btn"
                      >Download</a
                    >
                    <span
                      v-else
                      class="artifact-expired"
                      title="Generated files are kept for 30 minutes — ask the agent to regenerate the deck"
                      >Expired — ask to regenerate</span
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
                        <template v-if="!msg.script.expired">
                          <button
                            class="script-copy-btn"
                            :class="{
                              'script-copy-btn--copied':
                                copiedScriptId === msg.script.fileId,
                            }"
                            @click="copyScript(msg.script)"
                            :title="
                              copiedScriptId === msg.script.fileId
                                ? 'Copied!'
                                : 'Copy to clipboard'
                            "
                          >
                            <svg
                              v-if="copiedScriptId === msg.script.fileId"
                              width="14"
                              height="14"
                              viewBox="0 0 24 24"
                              fill="none"
                              stroke="currentColor"
                              stroke-width="2.5"
                              stroke-linecap="round"
                              stroke-linejoin="round"
                            >
                              <polyline points="20 6 9 17 4 12" />
                            </svg>
                            <svg
                              v-else
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
                        </template>
                        <span
                          v-else
                          class="artifact-expired"
                          title="Generated files are kept for 30 minutes — ask the agent to regenerate the script"
                          >Expired — ask to regenerate</span
                        >
                      </div>
                    </div>
                    <div
                      class="script-description"
                      v-if="msg.script.description"
                    >
                      {{ msg.script.description }}
                    </div>
                    <button
                      v-if="msg.script.content"
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
                  <div
                    v-else-if="streamReasoning"
                    class="stream-reasoning-block"
                  >
                    <div class="reasoning-label">
                      <span class="thinking-dots"><i></i><i></i><i></i></span>
                      Thinking
                    </div>
                    <div class="reasoning-body">
                      <div
                        class="reasoning-md"
                        v-html="renderContent(streamReasoning)"
                      ></div>
                    </div>
                  </div>
                  <div class="message-text" v-else-if="!streamIntent">
                    <span class="thinking-dots thinking-dots--lg"
                      ><i></i><i></i><i></i
                    ></span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- Floating "jump to latest" — shown while auto-follow is paused
             because the user scrolled up. Zero-height anchor keeps it glued
             to the bottom edge of the messages viewport. -->
        <div class="jump-latest-anchor">
          <!-- Plain v-show (no Transition): leave animations hang in hidden
               or timer-throttled views, leaving a ghost pill on screen. -->
          <button
            v-show="!stickToBottom && messages.length > 0"
            class="jump-latest"
            title="Jump to the latest message"
            @click="forceScrollToBottom(true)"
          >
            <svg
              width="13"
              height="13"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2.2"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path d="M12 5v14" />
              <path d="m19 12-7 7-7-7" />
            </svg>
            {{ streaming ? "New activity" : "Latest" }}
          </button>
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
            <div class="html-deck-card-title">
              {{ htmlCardTitle(htmlReady.slideCount) }}
            </div>
            <div class="html-deck-card-meta">
              {{ htmlCardMeta(htmlReady.slideCount)
              }}<span v-if="htmlReady.createdAt">
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
                  :class="{
                    'script-copy-btn--copied':
                      copiedScriptId === scriptReady.fileId,
                  }"
                  @click="copyScript(scriptReady)"
                  :title="
                    copiedScriptId === scriptReady.fileId
                      ? 'Copied!'
                      : 'Copy to clipboard'
                  "
                >
                  <svg
                    v-if="copiedScriptId === scriptReady.fileId"
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2.5"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <polyline points="20 6 9 17 4 12" />
                  </svg>
                  <svg
                    v-else
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
              CSV · TSV · JSON · TXT · XLSX · PDF · Parquet (≤ 100 MB) · PNG/JPG
              screenshots (≤ 20 MB)
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
                <img
                  v-if="att.thumbUrl"
                  :src="att.thumbUrl"
                  class="attachment-chip-thumb"
                  alt=""
                />
                <svg
                  v-else
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
              @paste="onPaste"
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
                  accept=".csv,.tsv,.json,.txt,.log,.md,.xlsx,.xls,.pdf,.parquet,.png,.jpg,.jpeg,.gif,.webp"
                  style="display: none"
                  @change="onFilePicked"
                />
                <button
                  class="input-action-btn"
                  :disabled="streaming"
                  @click="openFilePicker"
                  title="Attach file (CSV, JSON, TXT, XLSX, PDF, Parquet, PNG/JPG screenshots — or paste an image)"
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
        <div
          class="tools-sidebar-pane tools-sidebar-pane--agent"
          :class="{ 'tools-sidebar-pane--collapsed': agentCollapsed }"
        >
          <div class="tools-sidebar-header">
            <div
              class="tools-sidebar-header-text jobs-header-toggle"
              role="button"
              tabindex="0"
              @click="togglePane('agent')"
              @keydown.enter.prevent="togglePane('agent')"
              @keydown.space.prevent="togglePane('agent')"
              :title="
                agentCollapsed
                  ? 'Expand Agent execution'
                  : 'Collapse Agent execution'
              "
            >
              <span class="tools-sidebar-title"
                ><span
                  class="jobs-chevron"
                  :class="{ 'jobs-chevron--collapsed': agentCollapsed }"
                  >▾</span
                >Agent execution</span
              >
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
                :class="[
                  'st-row',
                  'st-row--cooler',
                  { 'st-row--cooler-open': tc.expanded },
                ]"
                @click.stop="tc.expanded = !tc.expanded"
                :title="`Cooling down ${tc.tool} (HTTP ${tc.status}) attempt ${tc.attempt}, waiting ${Math.round(tc.wait)}s`"
              >
                <svg
                  class="st-icon st-icon--cooler"
                  viewBox="0 0 16 16"
                  fill="none"
                >
                  <circle
                    cx="8"
                    cy="8"
                    r="6"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-dasharray="6 4"
                  >
                    <animateTransform
                      attributeName="transform"
                      type="rotate"
                      from="0 8 8"
                      to="360 8 8"
                      dur="1.4s"
                      repeatCount="indefinite"
                    />
                  </circle>
                </svg>
                <span class="st-name"
                  >Cooling down… {{ Math.round(tc.wait) }}s (attempt
                  {{ tc.attempt }})</span
                >
                <span class="st-time">HTTP {{ tc.status }}</span>
                <div v-if="tc.expanded" class="st-cooler-detail" @click.stop>
                  <div class="st-cooler-row">
                    <strong>Tool:</strong> {{ tc.tool }}
                  </div>
                  <div class="st-cooler-row">
                    <strong>Status:</strong> HTTP {{ tc.status }}
                  </div>
                  <div class="st-cooler-row">
                    <strong>Attempt:</strong> {{ tc.attempt }} of 5
                  </div>
                  <div class="st-cooler-row">
                    <strong>Wait:</strong> {{ Math.round(tc.wait) }}s
                  </div>
                  <div class="st-cooler-row st-cooler-url">
                    <strong>URL:</strong> <code>{{ tc.url }}</code>
                  </div>
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
          :class="{ 'tools-sidebar-pane--collapsed': sessionsCollapsed }"
        >
          <div class="tools-sidebar-header sessions-header">
            <div
              class="tools-sidebar-header-text jobs-header-toggle"
              role="button"
              tabindex="0"
              @click="togglePane('sessions')"
              @keydown.enter.prevent="togglePane('sessions')"
              @keydown.space.prevent="togglePane('sessions')"
              :title="
                sessionsCollapsed
                  ? 'Expand Conversations'
                  : 'Collapse Conversations'
              "
            >
              <span class="tools-sidebar-title"
                ><span
                  class="jobs-chevron"
                  :class="{ 'jobs-chevron--collapsed': sessionsCollapsed }"
                  >▾</span
                >Conversations</span
              >
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
              {{
                azureConnected
                  ? "No saved conversations yet — chat to create one."
                  : "Chat freely — connect Azure to keep conversations across visits."
              }}
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
        <!-- ── Scheduled jobs: prompt + cadence, runs in the background even
             when the browser is closed. Entra-only (needs the persisted
             refresh token). Each job gets its own conversation. ── -->
        <div
          class="tools-sidebar-pane tools-sidebar-pane--jobs"
          :class="{ 'tools-sidebar-pane--collapsed': jobsCollapsed }"
        >
          <div class="tools-sidebar-header sessions-header">
            <div
              class="tools-sidebar-header-text jobs-header-toggle"
              role="button"
              tabindex="0"
              @click="toggleJobsPane"
              @keydown.enter.prevent="toggleJobsPane"
              @keydown.space.prevent="toggleJobsPane"
              :title="jobsCollapsed ? 'Expand jobs' : 'Collapse jobs'"
            >
              <span class="tools-sidebar-title"
                ><span
                  class="jobs-chevron"
                  :class="{ 'jobs-chevron--collapsed': jobsCollapsed }"
                  >▾</span
                >Scheduled jobs</span
              >
              <span class="tools-sidebar-status">
                <span class="tools-sidebar-status-text"
                  >{{ activeJobsCount }} active</span
                >
                <span v-if="attentionJobsCount" class="jobs-attention"
                  >· {{ attentionJobsCount }} failing</span
                >
              </span>
            </div>
            <button
              class="sessions-new-btn"
              :disabled="!azureConnected || newJobOpen"
              @click="openNewJob"
              :title="
                azureConnected
                  ? 'Schedule a recurring prompt'
                  : 'Connect Azure to schedule background jobs'
              "
            >
              + New job
            </button>
          </div>
          <div class="tools-sidebar-scroll jobs-scroll">
            <div v-if="!azureConnected" class="sessions-empty">
              Connect Azure to run prompts on a schedule — daily cost reports,
              capacity hunting, anomaly checks.
            </div>
            <div v-else-if="jobs.length === 0" class="sessions-empty">
              No jobs yet — run FinOps checks on a schedule.
              <button class="jobs-empty-cta" @click="openNewJob">
                ＋ Schedule your first job
              </button>
            </div>
            <div
              v-for="j in sortedJobs"
              :key="j.id"
              :class="[
                'session-row',
                'job-row',
                { 'session-row--current': j.sessionId === currentSessionId },
                { 'job-row--paused': !j.enabled },
              ]"
              @click="openJob(j)"
              :title="jobTooltip(j)"
            >
              <span
                class="tools-sidebar-status-dot"
                :class="{
                  'tools-sidebar-status-dot--live': j.running,
                  'job-dot--dead':
                    !j.running &&
                    (j.lastStatus === 'error' ||
                      j.lastStatus === 'auth_expired'),
                  'job-dot--alive':
                    !j.running &&
                    j.enabled &&
                    j.lastStatus !== 'error' &&
                    j.lastStatus !== 'auth_expired',
                }"
                :title="
                  j.running
                    ? 'Running now'
                    : j.enabled
                      ? 'Scheduled — runs ' + formatUntil(j.nextRunUtc)
                      : 'Paused'
                "
              ></span>
              <div class="session-row-main">
                <span class="session-row-title">{{ j.name }}</span>
                <span class="session-row-time">
                  <template v-if="j._toggleErr">
                    <span class="job-time--dead">{{ j._toggleErr }}</span>
                  </template>
                  <template v-else-if="j.running">
                    <span class="job-time--running">running now…</span>
                  </template>
                  <template v-else-if="!j.enabled">
                    <template v-if="j.lastStatus === 'expired'">
                      {{ jobCadenceLabel(j) }} · expired
                    </template>
                    <template v-else-if="j.lastStatus === 'error'">
                      <span class="job-time--dead"
                        >failed {{ formatAgo(j.lastRunUtc) }}</span
                      >
                      · paused
                    </template>
                    <template v-else-if="j.lastStatus === 'auth_expired'">
                      <span class="job-time--dead">reconnect Azure</span> ·
                      paused
                    </template>
                    <template v-else-if="j.lastRunUtc">
                      {{ jobCadenceLabel(j) }} · paused · ran
                      {{ formatAgo(j.lastRunUtc) }}
                    </template>
                    <template v-else
                      >{{ jobCadenceLabel(j) }} · paused</template
                    >
                  </template>
                  <template v-else-if="j.lastStatus === 'auth_expired'">
                    {{ jobCadenceLabel(j) }} ·
                    <span class="job-time--dead">reconnect Azure</span>
                  </template>
                  <template v-else-if="j.lastStatus === 'error'">
                    <span class="job-time--dead"
                      >failed {{ formatAgo(j.lastRunUtc) }}</span
                    >
                    · next {{ formatUntil(j.nextRunUtc) }}
                  </template>
                  <template v-else-if="j.lastRunUtc">
                    {{ jobCadenceLabel(j) }} · ran
                    {{ formatAgo(j.lastRunUtc) }} · next
                    {{ formatUntil(j.nextRunUtc) }}
                  </template>
                  <template v-else>
                    {{ jobCadenceLabel(j) }} · first run
                    {{ formatUntil(j.nextRunUtc) }}
                  </template>
                </span>
              </div>
              <div class="job-actions">
                <button
                  class="job-row-btn"
                  @click.stop="runJobNow(j)"
                  :disabled="j.running"
                  title="Run once now"
                  aria-label="Run job now"
                >
                  ▶
                </button>
                <button
                  class="job-row-btn"
                  @click.stop="editJob(j)"
                  title="Edit job"
                  aria-label="Edit job"
                >
                  ✎
                </button>
                <button
                  class="job-switch"
                  :class="{ 'job-switch--on': j.enabled }"
                  role="switch"
                  :aria-checked="j.enabled ? 'true' : 'false'"
                  @click.stop="toggleJob(j)"
                  :title="
                    j.enabled
                      ? 'Schedule is on — click to pause'
                      : 'Schedule is off — click to resume'
                  "
                  aria-label="Toggle schedule"
                >
                  <span class="job-switch-knob"></span>
                </button>
                <button
                  class="session-row-delete"
                  @click.stop="deleteJob(j)"
                  title="Delete this job (its conversation is kept)"
                  aria-label="Delete job"
                >
                  ×
                </button>
              </div>
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

    <!-- New scheduled job — centered modal sheet, above everything -->
    <Teleport to="body">
      <div
        v-if="newJobOpen"
        class="job-modal-overlay"
        @click.self="closeJobModal"
        @keydown.esc="closeJobModal"
      >
        <div
          class="job-modal"
          role="dialog"
          aria-modal="true"
          :aria-label="
            editingJobId ? 'Edit scheduled job' : 'Schedule a background job'
          "
        >
          <div class="job-modal-header">
            <div class="job-modal-icon">⚙</div>
            <div class="job-modal-heading">
              <div class="job-modal-title">
                {{
                  editingJobId
                    ? "Edit scheduled job"
                    : "Schedule a background job"
                }}
              </div>
              <div class="job-modal-subtitle">
                Runs in the background — even with the browser closed — using
                only your delegated Azure permissions: the agent can never do
                more than your own login allows. Every run's output lands in the
                job's conversation.
              </div>
            </div>
            <button
              class="job-modal-close"
              @click="closeJobModal"
              aria-label="Close"
            >
              ×
            </button>
          </div>
          <div class="job-modal-body">
            <div v-if="!editingJobId" class="job-tpl-label">
              Start from a template
            </div>
            <div v-if="!editingJobId" class="job-tpl-grid">
              <button
                v-for="t in JOB_TEMPLATES"
                :key="t.label"
                class="job-tpl"
                :class="{ 'job-tpl--selected': selectedTemplate === t.label }"
                :title="t.prompt"
                @click="applyTemplate(t)"
              >
                <span class="job-tpl-emoji">{{ t.emoji }}</span>
                <span class="job-tpl-name">{{ t.label }}</span>
              </button>
            </div>
            <div class="job-tpl-label">Name</div>
            <input
              v-model="newJobName"
              class="job-form-input"
              type="text"
              maxlength="60"
              placeholder="Optional — e.g. H100 capacity hunt"
            />
            <div class="job-tpl-label">What should the agent do every run?</div>
            <textarea
              ref="jobPromptEl"
              v-model="newJobPrompt"
              class="job-form-prompt"
              rows="3"
              maxlength="2000"
              placeholder="e.g. Check ND96isr H200 v5 spot capacity and quota in all my subscriptions across all regions; if deployable, tell me exactly where."
              @input="autosizeJobPrompt"
              @keydown.ctrl.enter.prevent="createJob"
              @keydown.meta.enter.prevent="createJob"
            ></textarea>
            <div class="job-tpl-label">How often?</div>
            <div class="job-freq-row">
              <button
                v-for="f in JOB_FREQUENCIES"
                :key="f.v"
                class="job-freq"
                :class="{ 'job-freq--selected': newJobInterval === f.v }"
                @click="newJobInterval = f.v"
              >
                {{ f.l }}
              </button>
            </div>
            <div class="job-freq-custom">
              <label class="job-freq-custom-label">
                or run every
                <input
                  type="number"
                  class="job-freq-custom-input"
                  :min="MIN_INTERVAL"
                  :max="MAX_INTERVAL"
                  step="1"
                  v-model.number="newJobInterval"
                  @keydown.ctrl.enter.prevent="createJob"
                  @keydown.meta.enter.prevent="createJob"
                />
                min
              </label>
              <span class="job-freq-expiry">{{
                newJobInterval < 1440
                  ? "expires after 7 days"
                  : "expires after 90 days"
              }}</span>
            </div>
            <div v-if="!jobIntervalValid" class="job-form-error">
              Enter a cadence between {{ MIN_INTERVAL }} and
              {{ MAX_INTERVAL }} minutes.
            </div>
            <label class="job-runnow">
              <input v-model="newJobRunNow" type="checkbox" />
              <span>
                {{
                  editingJobId
                    ? "Run now after saving"
                    : "Run immediately, then repeat on the schedule"
                }}
                <span class="job-runnow-hint">{{
                  editingJobId
                    ? newJobRunNow
                      ? "— triggers a run as soon as you save"
                      : "— just updates the schedule"
                    : newJobRunNow
                      ? "— first result lands in its conversation within a minute"
                      : "— first run waits one full interval"
                }}</span>
              </span>
            </label>
            <div v-if="jobError" class="job-form-error">{{ jobError }}</div>
          </div>
          <div class="job-modal-footer">
            <span class="job-form-hint">{{
              editingJobId
                ? "Ctrl+Enter saves"
                : "Max 3 active jobs · Ctrl+Enter creates"
            }}</span>
            <div class="job-form-actions">
              <button class="job-form-cancel" @click="closeJobModal">
                Cancel
              </button>
              <button
                class="job-form-create"
                :disabled="!newJobPrompt.trim() || jobBusy || !jobIntervalValid"
                @click="createJob"
              >
                {{
                  jobBusy
                    ? editingJobId
                      ? "Saving…"
                      : "Creating…"
                    : editingJobId
                      ? "Save changes"
                      : "Create job"
                }}
              </button>
            </div>
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
const streamReasoning = ref("");
const htmlReady = ref(null);
const scriptReady = ref(null);
const messagesEl = ref(null);
const inputEl = ref(null);
const chartInstances = [];
// ResizeObservers created per mounted chart — tracked so wipes/unmount can
// disconnect them (they'd otherwise keep observing detached DOM forever).
const chartResizeObservers = [];
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

const IMAGE_KINDS = new Set(["image", "png", "jpg", "jpeg", "gif", "webp"]);
const isImageAttachment = (att) =>
  IMAGE_KINDS.has((att.kind || "").toLowerCase());

// Paste-to-attach: screenshots copied to the clipboard (Win+Shift+S, browser
// right-click → Copy image, etc.) upload straight from Ctrl+V in the input.
// Text pastes are untouched — we only intercept when an image file is present.
async function onPaste(ev) {
  const items = Array.from(ev.clipboardData?.items || []);
  const imageItems = items.filter(
    (it) => it.kind === "file" && it.type.startsWith("image/"),
  );
  if (!imageItems.length) return;
  ev.preventDefault();
  const stamp = new Date().toISOString().replace(/[-:T]/g, "").slice(0, 14);
  const files = imageItems
    .map((it, i) => {
      const f = it.getAsFile();
      if (!f) return null;
      // Clipboard images arrive as a generic "image.png" — give screenshots a
      // distinguishable, timestamped name.
      const ext = (f.type.split("/")[1] || "png").replace("jpeg", "jpg");
      const generic = !f.name || /^image\.(png|jpe?g|gif|webp)$/i.test(f.name);
      const name = generic
        ? `screenshot-${stamp}${i ? `-${i + 1}` : ""}.${ext}`
        : f.name;
      return new File([f], name, { type: f.type });
    })
    .filter(Boolean);
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
      // Local thumbnail for image chips — revoked when the chip is removed.
      thumbUrl: file.type?.startsWith("image/")
        ? URL.createObjectURL(file)
        : null,
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
  if (att.thumbUrl) {
    try {
      URL.revokeObjectURL(att.thumbUrl);
    } catch {}
  }
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
  image: "Screenshot / image",
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
  if (isImageAttachment({ kind })) return PURPOSE_LABELS.image;
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
  // Image-only attachments: the screenshots ride along as vision input — no
  // QueryUploadedFile involved. Ask for a visual read instead of schema calls.
  if (list.every(isImageAttachment)) {
    const names = list.map((a) => `'${a.fileName}'`).join(", ");
    return `Look at the attached ${list.length === 1 ? "screenshot" : `${list.length} screenshots`} (${names}). Describe what it shows, extract every number/cost/resource name visible, and give the top FinOps insights or anomalies you can read from it. If it shows an Azure portal view (cost analysis, Advisor, resource list), relate what you see to concrete next optimization actions. Then **call SuggestFollowUp with 3 distinct next actions**.`;
  }
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
  // Background tab: requestAnimationFrame is fully suspended, so the rAF
  // drain below never runs and text piles up invisibly — the AI looked
  // "stopped" until the tab was refocused (and then crawled at ~600 chars/s).
  // When hidden, skip the animation entirely and render synchronously.
  if (document.hidden) {
    flushText();
    streamBuffer.value += text;
    return;
  }
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

// Flush the animation queue the moment the tab goes to the background — any
// chars already queued for rAF would otherwise be frozen mid-word until the
// user returns. (New chars arriving while hidden take the synchronous path
// in enqueueText above; the SSE reader itself is not throttled by browsers.)
//
// Backgrounding/minimizing is ALSO where connections break. On desktop the
// fetch usually stays alive (throttled) and self-heals on return; on mobile or
// aggressive tab-freezing it can be severed. The backend never aborts the turn
// on client disconnect — it keeps generating and persists the answer to disk
// (see ChatEndpoints RequestAborted handler) — so recovery is always a matter
// of reloading the persisted transcript, NOT re-running anything.
//
// Critical: we do NOT abort or reload a still-running stream on a client-side
// timer. Reasoning models are legitimately silent for many seconds (time to
// first token was measured at ~3.5s; long tool calls far longer), so any
// "no bytes for N ms ⇒ dead" heuristic produces false positives that kill
// healthy answers. We only reconcile once the client stream has actually ended.
function onVisibilityChange() {
  if (document.hidden) {
    flushText();
    const sid = currentSessionId.value;
    hiddenDuringStream = !!(sid && runningSessions.has(sid));
    window.__trackAppInsightsEvent?.("chat.tab.hidden", {
      sessionId: sid || "",
      streaming: String(streaming.value),
      runningWhenHidden: String(hiddenDuringStream),
    });
    return;
  }

  const sid = currentSessionId.value;
  const wasStreaming = hiddenDuringStream;
  hiddenDuringStream = false;
  window.__trackAppInsightsEvent?.("chat.tab.visible", {
    sessionId: sid || "",
    wasStreamingWhenHidden: String(wasStreaming),
    stillRunning: String(!!(sid && runningSessions.has(sid))),
  });

  // Converge the viewport: content rendered synchronously while hidden
  // (rAF and ResizeObserver delivery are suspended in background tabs) —
  // pin back to the newest content if the user was following.
  scrollToBottom();

  // The "come back" moment: refresh job statuses immediately so the pane shows
  // what ran while the tab was away (fresh lastRun/nextRun/running states).
  loadJobs();

  // Always reconcile on return — covers both "stream ended while away" (reload
  // the persisted answer) and "stream still running" (arm the zombie probe in
  // case the connection died silently while frozen). No-ops fast when idle.
  if (sid) reconcileSessionAfterReturn(sid, "visibility");
}

// Backup trigger: some browsers (notably Firefox) don't fire visibilitychange
// when a window is minimized/restored, but window focus always fires.
function onWindowFocus() {
  const sid = currentSessionId.value;
  if (sid) reconcileSessionAfterReturn(sid, "focus");
}

// Non-destructive recovery after the tab/window regains focus.
// • Stream still running client-side → arm the zombie probe: it watches the
//   SERVER's persisted transcript and only aborts the client stream once the
//   answer is provably on disk yet undelivered (never a client-silence timer,
//   which would kill healthy streams during long think/tool phases).
// • Stream already ended without showing the answer (severed while frozen →
//   "Connection lost", or nothing rendered) → recover from the persisted
//   transcript via the tail-match poller.
//
// True if an assistant message actually carries a delivered answer — text, a
// chart, a generated deck/script, or completed tool calls. A bare "Connection
// lost" placeholder (severed stream) does NOT count, so recovery still fires
// for it. Used to decide whether a returning tab is still waiting on an answer
// or already has one.
function assistantTurnHasOutput(m) {
  if (!m || m.role !== "assistant") return false;
  const text = String(m.content || "");
  if (text.includes("Connection lost")) return false;
  return !!(
    text.trim() ||
    (m.charts && m.charts.length) ||
    m.html ||
    m.script ||
    (m.toolCalls && m.toolCalls.length)
  );
}

async function reconcileSessionAfterReturn(sid, reason) {
  if (!sid || sid === "__pending__") return;
  if (runningSessions.has(sid)) {
    startZombieProbe(sid);
    return;
  }
  // Only reconcile when THIS view is actually waiting on an answer: the last
  // question the user asked has no answer yet (or the answer failed with a
  // "Connection lost" placeholder). An empty/new conversation — or one that
  // already ends in a real answer — has nothing to recover. Reconnecting on
  // those was the bug: a fresh session flashed a phantom "⏳ Reconnecting…"
  // notice (and polled for 3 min) even though no turn was ever in flight.
  const msgs = messages.value;
  let lastUserIdx = -1;
  for (let i = msgs.length - 1; i >= 0; i--) {
    if (msgs[i].role === "user") {
      lastUserIdx = i;
      break;
    }
  }
  if (lastUserIdx < 0) return; // no question in this view — nothing to reconnect
  let answered = false;
  for (let i = lastUserIdx + 1; i < msgs.length; i++) {
    if (assistantTurnHasOutput(msgs[i])) {
      answered = true;
      break;
    }
  }
  if (answered) return; // answer already visible — nothing to do
  window.__trackAppInsightsEvent?.("chat.reattach.recover", {
    sessionId: sid,
    reason: reason || "",
  });
  const lastUser = msgs[lastUserIdx];
  await tryRecoverPersistedAnswer(sid, String(lastUser.content || ""));
}

// ── Persisted-transcript recovery ──
// The backend never aborts a turn on client disconnect: the CLI keeps
// generating and persists events to the on-disk session state as it goes. So
// whenever the client's SSE dies (background-tab freeze, minimize, network
// drop, page reload), the answer WILL appear in /api/sessions/{id}/messages —
// we just have to wait for it and repaint. Note the turn-active probe is
// useless here: ChatEndpoints removes the turn from ActiveTurns the moment the
// SSE handler unwinds, seconds before the answer lands.

const normText = (s) =>
  String(s || "")
    .replace(/\s+/g, " ")
    .trim()
    .toLowerCase();

// Reads the persisted tail for a session: does the LAST user message match the
// prompt we're recovering, and does a non-empty assistant answer follow it?
async function fetchPersistedTail(sid, normPrompt) {
  try {
    const r = await fetch(`/api/sessions/${encodeURIComponent(sid)}/messages`);
    if (!r.ok) return null;
    const msgs = (await r.json()).messages || [];
    let lastUserIdx = -1;
    for (let i = msgs.length - 1; i >= 0; i--) {
      if (msgs[i].role === "user") {
        lastUserIdx = i;
        break;
      }
    }
    if (lastUserIdx < 0) return { matched: false, answered: false, ansLen: 0 };
    const u = normText(msgs[lastUserIdx].content);
    const matched =
      !normPrompt ||
      u === normPrompt ||
      u.includes(normPrompt.slice(0, 80)) ||
      normPrompt.includes(u.slice(0, 80));
    let ansLen = 0;
    let answered = false;
    for (let i = lastUserIdx + 1; i < msgs.length; i++) {
      if (msgs[i].role === "assistant") {
        const c = String(msgs[i].content || "").trim();
        if (c) {
          answered = true;
          ansLen += c.length;
        }
      }
    }
    return { matched, answered, ansLen };
  } catch {
    return null;
  }
}

// Exact text of the recovery banner. Kept as a constant + marked with a
// `_reconnect` flag on the message so we can add/remove it PRECISELY without
// also clobbering the "busy" notice (which also starts with ⏳). Matching on
// startsWith("⏳") used to nuke both.
const RECONNECT_NOTICE_TEXT =
  "⏳ Reconnecting — your last answer is still being generated and will appear here when ready.";

// Ensures EXACTLY ONE reconnect banner exists in the current view, as the last
// row. Idempotent + cheap: no-ops when it's already there, so it can be called
// on every poll tick without churning reactivity or the scroll position.
function setReconnectNotice() {
  const msgs = messages.value;
  const already =
    msgs.length > 0 &&
    msgs[msgs.length - 1]._reconnect &&
    msgs.filter((m) => m._reconnect).length === 1;
  if (already) return;
  messages.value = [
    ...msgs.filter((m) => !m._reconnect),
    { role: "system", content: RECONNECT_NOTICE_TEXT, _reconnect: true },
  ];
}
// Removes every reconnect banner from the current view. Never touches the
// "busy" notice or any other system/assistant message.
function clearReconnectNotice() {
  if (messages.value.some((m) => m._reconnect)) {
    messages.value = messages.value.filter((m) => !m._reconnect);
  }
}

// Recover the answer for a lost turn from the persisted transcript. Polls the
// tail until the assistant's answer for THIS prompt appears (instant if it
// already landed while the tab was away), repaints, then keeps watching
// briefly: if the persisted answer keeps growing (mid-turn narration → final
// answer), repaint again so the UI always converges on the final state.
// Returns true once recovered; false on timeout/abandon. ALWAYS leaves at most
// one banner behind and clears it on every exit path — an orphaned/duplicated
// "Reconnecting…" banner that never converges was the bug this guards against.
async function tryRecoverPersistedAnswer(sid, promptText) {
  if (!sid || sid === "__pending__") return false;
  // No specific in-flight turn to recover (empty/new conversation) — never show
  // the "⏳ Reconnecting…" notice or start the poller for nothing.
  if (!String(promptText || "").trim()) return false;
  if (reconcilingSid === sid) return false; // a poller is already on it
  const normPrompt = normText(promptText);
  // "Is this recovering turn still the one on screen?" — the gate for painting
  // into the view. NOT a strict currentSessionId===sid check: for ANON users
  // loadSessions() resets currentSessionId to null after every turn (it runs in
  // send()'s finally), so strict equality would make recovery stand down
  // instantly and SILENTLY — no banner, no answer. Treat null as "still here"
  // but confirm the recovering prompt is still the last question displayed, so a
  // "New chat" or a switch to another conversation correctly hands off the view.
  const isVisible = () => {
    const cur = currentSessionId.value;
    if (cur && cur !== sid) return false; // switched to a different conversation
    const msgs = messages.value;
    for (let i = msgs.length - 1; i >= 0; i--) {
      if (msgs[i].role === "user") {
        const u = normText(msgs[i].content);
        return (
          u === normPrompt ||
          u.includes(normPrompt.slice(0, 40)) ||
          normPrompt.includes(u.slice(0, 40))
        );
      }
    }
    return false; // view cleared / brand-new session — hands off
  };
  reconcilingSid = sid;
  let recovered = false;
  let bannerShown = false;
  try {
    // Cover the backend's full per-turn budget (ActiveTurns staleness cap is
    // 15 min): a "score my whole estate / all regions × all subscriptions"
    // turn makes dozens of tool calls and persists NOTHING until the final
    // answer, so the old 3-min deadline gave up while the CLI was still
    // working — leaving a permanently stuck "Reconnecting…" banner. Poll
    // interval backs off 3s→10s to keep the request count sane over 15 min.
    const deadline = Date.now() + 900000; // 15 min
    let pollMs = 3000;
    while (Date.now() < deadline) {
      // The user started a NEW live turn in this view — the stream owns the UI
      // now; stand down and clear our banner.
      if (streaming.value) {
        clearReconnectNotice();
        return false;
      }
      const tail = await fetchPersistedTail(sid, normPrompt);
      if (tail && tail.matched && tail.answered) {
        await reloadSessionTranscript(sid);
        recovered = true;
        window.__trackAppInsightsEvent?.("chat.stream.recovered", {
          sessionId: sid,
          polled: String(bannerShown),
        });
        // Refinement: the persisted text may still be mid-turn narration. Keep
        // watching briefly; repaint whenever it grows, stop once stable.
        let lastLen = tail.ansLen;
        let stable = 0;
        const refineDeadline = Date.now() + 60000;
        while (Date.now() < refineDeadline && stable < 3) {
          await new Promise((r) => setTimeout(r, 4000));
          if (!isVisible() || streaming.value) break;
          const t2 = await fetchPersistedTail(sid, normPrompt);
          if (!t2 || !t2.matched) break;
          if (t2.ansLen !== lastLen) {
            lastLen = t2.ansLen;
            stable = 0;
            await reloadSessionTranscript(sid);
          } else {
            stable++;
          }
        }
        return true;
      }
      // Still generating — show the (single, deduped) banner while the user is
      // looking. If they've navigated away, stand down (their view is theirs).
      if (!isVisible()) return false;
      setReconnectNotice();
      bannerShown = true;
      await new Promise((r) => setTimeout(r, pollMs));
      pollMs = Math.min(pollMs + 1000, 10000);
    }
    // Deadline hit without an answer landing. If the user is still on this
    // conversation and it's genuinely still unanswered, replace the banner with
    // an actionable retry instead of a dead "Reconnecting…" banner or silent
    // blank. Re-sync auth + sidebar — a server restart can invalidate the
    // session while the UI still shows "connected".
    if (isVisible()) {
      clearReconnectNotice();
      const msgs = messages.value;
      let lastUserIdx = -1;
      for (let i = msgs.length - 1; i >= 0; i--) {
        if (msgs[i].role === "user") {
          lastUserIdx = i;
          break;
        }
      }
      let resolved = false;
      for (let i = lastUserIdx + 1; i < msgs.length; i++) {
        const m = msgs[i];
        if (
          assistantTurnHasOutput(m) ||
          (m.role === "assistant" &&
            String(m.content).includes("Connection lost"))
        ) {
          resolved = true;
          break;
        }
      }
      if (lastUserIdx >= 0 && !resolved) {
        messages.value = [
          ...messages.value,
          {
            role: "assistant",
            content:
              "**Connection lost** — the answer took longer than expected or the connection dropped. Your conversation is saved and may still finish; retry if it doesn't appear.",
            followUp: { label: "Retry last question", prompt: promptText },
          },
        ];
        window.__trackAppInsightsEvent?.("chat.stream.recoverTimeout", {
          sessionId: sid,
        });
        checkAzureStatus();
        loadSessions();
      }
    }
  } catch (e) {
    window.__trackAppInsightsException?.(e, {
      source: "tryRecoverPersistedAnswer",
      sessionId: sid,
    });
  } finally {
    if (reconcilingSid === sid) reconcilingSid = null;
    // Belt-and-braces: never leave an orphaned banner behind on ANY exit path
    // (early return, recovery, timeout, throw). Only touch the view if this turn
    // is still the one on screen — don't clear a conversation the user swapped
    // to. (On recovery, reloadSessionTranscript already rebuilt the list.)
    if (!recovered && isVisible()) clearReconnectNotice();
  }
  return recovered;
}

// Watches a client stream that SHOULD still be running after the tab returns
// to the foreground. If the server's persisted transcript already contains the
// answer for the in-flight prompt but the client stream still hasn't delivered
// it one confirm-tick later, the connection died silently while the tab was
// frozen (no rejection ever fired) — abort it with the reconcile flag so the
// send() catch recovers the persisted answer immediately. Driven purely by
// SERVER state; a healthy stream that is merely slow never trips it because
// its answer isn't persisted until generation completes — at which point a
// healthy stream finishes within a tick anyway.
function startZombieProbe(sid) {
  const token = ++zombieProbeToken;
  let confirmed = false;
  const tick = async () => {
    if (token !== zombieProbeToken) return; // superseded
    if (!runningSessions.has(sid)) return; // stream ended naturally — done
    const turn = inFlightTurn;
    const normPrompt = turn && turn.sid === sid ? normText(turn.prompt) : "";
    const tail = await fetchPersistedTail(sid, normPrompt);
    if (token !== zombieProbeToken || !runningSessions.has(sid)) return;
    if (tail && tail.matched && tail.answered) {
      if (confirmed) {
        // Answer on disk, stream still hanging one tick later → zombie.
        window.__trackAppInsightsEvent?.("chat.stream.zombieRecover", {
          sessionId: sid,
        });
        reconcileAbort = true;
        try {
          abortController?.abort();
        } catch {}
        return;
      }
      confirmed = true;
    } else {
      confirmed = false;
    }
    window.setTimeout(tick, 4000);
  };
  window.setTimeout(tick, 4000);
}

const hoveredTool = ref(null);
const collapsedSections = reactive({
  subs: true,
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
const availableModels = ref(["gpt-5.6-sol"]);
const selectedModel = ref("gpt-5.6-sol");

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
  // Piggyback: jobs status refreshes on the same cadence as the sidebar
  // (mount + after every turn) — no dedicated poll loop needed.
  loadJobs();
}

// ── Scheduled jobs (Entra-only) ───────────────────────────────
// A job = prompt + cadence, executed by the backend JobScheduler inside a
// dedicated conversation (the job row opens it like any other session).
const jobs = ref([]);
const newJobOpen = ref(false);
const newJobName = ref("");
const newJobPrompt = ref("");
const newJobInterval = ref(1440);
const newJobRunNow = ref(true);
// Non-null when the modal is in EDIT mode (id of the job being edited).
const editingJobId = ref(null);
const jobBusy = ref(false);
const jobError = ref("");
// Collapsible panes — each of the three sidebar sections (Agent /
// Conversations / Scheduled jobs) can be collapsed to its header so the
// remaining pane(s) get all the vertical space (focus mode). Sticky per pane.
function loadPaneCollapsed(key) {
  try {
    return localStorage.getItem(`finops.paneCollapsed.${key}`) === "1";
  } catch {
    return false;
  }
}
const agentCollapsed = ref(loadPaneCollapsed("agent"));
const sessionsCollapsed = ref(loadPaneCollapsed("sessions"));
const jobsCollapsed = ref(
  (() => {
    try {
      // migrate the older single-pane key if present
      return (
        localStorage.getItem("finops.paneCollapsed.jobs") === "1" ||
        localStorage.getItem("finops.jobsCollapsed") === "1"
      );
    } catch {
      return false;
    }
  })(),
);
function togglePane(key) {
  const map = {
    agent: agentCollapsed,
    sessions: sessionsCollapsed,
    jobs: jobsCollapsed,
  };
  const r = map[key];
  r.value = !r.value;
  try {
    localStorage.setItem(`finops.paneCollapsed.${key}`, r.value ? "1" : "0");
  } catch {}
}
function toggleJobsPane() {
  togglePane("jobs");
}
// Ready-made jobs — clicking one prefills the form (still fully editable).
// Kept deliberately concrete: these are the highest-value recurring asks.
// Capacity templates lead — constrained GPU/VM capacity is the single most
// common customer complaint, and hunt-then-act is this feature's sweet spot.
const JOB_TEMPLATES = [
  {
    emoji: "📡",
    label: "Check capacity of X",
    name: "Capacity check: Standard_NC40ads_H100_v5",
    interval: 15,
    prompt:
      "Check on-demand capacity and my quota for Standard_NC40ads_H100_v5 (replace with the VM size you're hunting) across all my subscriptions and all regions. Tell me exactly where it is deployable right now — region, subscription, quota headroom. If it's deployable nowhere, list the closest regions where only quota is missing. Read-only: do NOT create or reserve anything.",
  },
  {
    emoji: "🎯",
    label: "Reserve X when available",
    name: "Reserve Standard_NC40ads_H100_v5 when available",
    interval: 15,
    prompt:
      "Check quota and on-demand capacity for Standard_NC40ads_H100_v5 (replace with the VM size you want) across all my subscriptions and regions. IDEMPOTENCY FIRST: if a capacity reservation group named 'finops-capacity-crg' already exists in any region, do nothing and report what is already reserved. Otherwise, if the size is deployable AND I have quota headroom for it: secure it immediately by creating resource group 'finops-capacity' (if missing), capacity reservation group 'finops-capacity-crg', and an on-demand capacity reservation 'finops-reserve-1' with that size and capacity 1 in that region (ARM PUT). Then state exactly what was reserved, that it bills at the full VM rate while held, and that I should pause this job and delete the reservation (you'll generate the cleanup script) when done. If my RBAC blocks the write, say so. If nothing is deployable, list the closest regions with quota headroom.",
  },
  {
    emoji: "⚡",
    label: "1-min test",
    name: "1-minute test",
    interval: 1,
    prompt:
      "Report my current month-to-date Azure consumption in USD. One short sentence.",
  },
  {
    emoji: "📊",
    label: "Daily cost digest",
    name: "Daily cost digest",
    interval: 1440,
    prompt:
      "Summarize yesterday's Azure spend by service and subscription, compare it to the 7-day average, and call out anything unusual with likely root causes.",
  },
  {
    emoji: "🚨",
    label: "Anomaly watch",
    name: "Cost anomaly watch",
    interval: 60,
    prompt:
      "Compare today's spend rate so far against the same window over the past 7 days. Flag any service or resource group trending more than 20% above normal and identify the resources driving it.",
  },
  {
    emoji: "💰",
    label: "Budget guard",
    name: "Budget guard",
    interval: 1440,
    prompt:
      "Check every budget and the month-end forecast across all my subscriptions. Warn me if any subscription or resource group is projected to exceed its budget, with the current burn rate.",
  },
  {
    emoji: "🧹",
    label: "Idle resource sweep",
    name: "Idle resource sweep",
    interval: 10080,
    prompt:
      "Find idle and orphaned resources — unattached disks, unused public IPs, stopped-but-allocated VMs, empty App Service plans — estimate the monthly waste and generate a cleanup script for review.",
  },
  {
    emoji: "💡",
    label: "Advisor watch",
    name: "Advisor cost watch",
    interval: 1440,
    prompt:
      "Check for new Azure Advisor cost recommendations and summarize the top opportunities with estimated monthly savings, ranked by impact and effort.",
  },
];
const selectedTemplate = ref("");
function applyTemplate(t) {
  selectedTemplate.value = t.label;
  newJobName.value = t.name;
  newJobPrompt.value = t.prompt;
  newJobInterval.value = t.interval;
  nextTick(autosizeJobPrompt);
}
// Cadence pills for the create-job modal.
const JOB_FREQUENCIES = [
  { v: 15, l: "Every 15 min" },
  { v: 60, l: "Hourly" },
  { v: 1440, l: "Daily" },
  { v: 10080, l: "Weekly" },
];
// Custom-cadence bounds — mirror the backend (1 min floor = scheduler tick, 30 day ceiling).
const MIN_INTERVAL = 1;
const MAX_INTERVAL = 43200;
const jobIntervalValid = computed(() => {
  const n = newJobInterval.value;
  return Number.isFinite(n) && n >= MIN_INTERVAL && n <= MAX_INTERVAL;
});
const jobPromptEl = ref(null);
// Auto-grow the prompt textarea so the FULL prompt is always visible — no
// inner scrollbar. Runs on typing, on template apply, and on modal open
// (the modal body scrolls if a very long prompt exceeds the sheet height).
function autosizeJobPrompt() {
  const el = jobPromptEl.value;
  if (!el) return;
  el.style.height = "auto";
  el.style.height = el.scrollHeight + 2 + "px";
}
// Display order for a long list: running first, then anything failing (needs
// the user), then armed jobs by soonest next run, then paused/expired.
const sortedJobs = computed(() => {
  const rank = (j) => {
    if (j.running) return 0;
    if (j.lastStatus === "error" || j.lastStatus === "auth_expired") return 1;
    if (j.enabled) return 2;
    return 3;
  };
  return [...jobs.value].sort((a, b) => {
    const r = rank(a) - rank(b);
    if (r !== 0) return r;
    if (rank(a) === 2)
      return new Date(a.nextRunUtc || 0) - new Date(b.nextRunUtc || 0);
    return new Date(b.lastRunUtc || 0) - new Date(a.lastRunUtc || 0);
  });
});
const activeJobsCount = computed(
  () => jobs.value.filter((j) => j.enabled).length,
);
const attentionJobsCount = computed(
  () =>
    jobs.value.filter(
      (j) => j.lastStatus === "error" || j.lastStatus === "auth_expired",
    ).length,
);
// Live clock for the "next in X min" countdowns — a 30 s ticker the template
// reads through formatUntil() so countdowns stay fresh without any list churn.
const nowTick = ref(Date.now());
let jobsTickTimer = null;
let jobsPollTimer = null;

async function loadJobs() {
  if (!azureConnected.value) {
    jobs.value = [];
    return;
  }
  try {
    const res = await fetch("/api/jobs", { credentials: "same-origin" });
    if (!res.ok) return;
    const data = await res.json();
    jobs.value = Array.isArray(data.jobs) ? data.jobs : [];
  } catch {
    /* network blip — keep prior list */
  }
}

function openNewJob() {
  jobError.value = "";
  editingJobId.value = null;
  selectedTemplate.value = "";
  // Start from a clean slate — otherwise a cancelled edit would leak its
  // name/prompt/cadence into the fresh "New job" form.
  newJobName.value = "";
  newJobPrompt.value = "";
  newJobInterval.value = 1440;
  newJobRunNow.value = true;
  jobsCollapsed.value = false;
  newJobOpen.value = true;
  // Land the cursor in the prompt — the one field that matters — and size it
  // to whatever content is already there (e.g. reopening with a draft).
  nextTick(() => {
    jobPromptEl.value?.focus();
    autosizeJobPrompt();
  });
}

// Close the modal and clear edit state so the next open starts clean.
function closeJobModal() {
  newJobOpen.value = false;
  editingJobId.value = null;
  jobError.value = "";
}

// Open the SAME modal prefilled to edit an existing job.
function editJob(j) {
  jobError.value = "";
  editingJobId.value = j.id;
  selectedTemplate.value = "";
  newJobName.value = j.name || "";
  newJobPrompt.value = j.prompt || "";
  newJobInterval.value = j.intervalMinutes || 1440;
  // Default OFF for edits — a normal edit shouldn't trigger a run; the user
  // opts in when they want to run the job right after saving.
  newJobRunNow.value = false;
  jobsCollapsed.value = false;
  newJobOpen.value = true;
  nextTick(() => {
    jobPromptEl.value?.focus();
    autosizeJobPrompt();
  });
}

async function createJob() {
  if (!newJobPrompt.value.trim() || jobBusy.value || !jobIntervalValid.value)
    return;
  jobBusy.value = true;
  jobError.value = "";
  try {
    // EDIT mode — PATCH the existing job (name / prompt / cadence).
    if (editingJobId.value) {
      const res = await fetch(
        `/api/jobs/${encodeURIComponent(editingJobId.value)}`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            name: newJobName.value.trim() || undefined,
            prompt: newJobPrompt.value.trim(),
            intervalMinutes: Math.round(newJobInterval.value),
          }),
        },
      );
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        jobError.value = data.error || "Failed to save changes.";
        return;
      }
      const jobId = editingJobId.value;
      const runAfterSave = newJobRunNow.value;
      newJobOpen.value = false;
      editingJobId.value = null;
      newJobName.value = "";
      newJobPrompt.value = "";
      newJobInterval.value = 1440;
      window.__trackAppInsightsEvent?.("jobs.edited", {
        ranAfter: String(runAfterSave),
      });
      // "Run now after saving" — fire an immediate run of the just-edited job.
      if (runAfterSave && jobId) {
        try {
          await fetch(`/api/jobs/${encodeURIComponent(jobId)}/run`, {
            method: "POST",
          });
        } catch {}
        // Open the run's conversation once its session exists (also refreshes
        // the jobs list as it polls).
        watchJobRunAndOpen(jobId);
        return;
      }
      await loadJobs();
      return;
    }
    const res = await fetch("/api/jobs", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: newJobName.value.trim() || undefined,
        prompt: newJobPrompt.value.trim(),
        intervalMinutes: Math.round(newJobInterval.value),
        runImmediately: newJobRunNow.value,
      }),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      jobError.value = data.error || "Failed to create job.";
      return;
    }
    newJobOpen.value = false;
    newJobName.value = "";
    newJobPrompt.value = "";
    newJobInterval.value = 1440;
    window.__trackAppInsightsEvent?.("jobs.created", {
      intervalMinutes: String(data.intervalMinutes || ""),
    });
    await loadJobs();
  } finally {
    jobBusy.value = false;
  }
}

async function toggleJob(j) {
  const wasPaused = !j.enabled;
  try {
    const res = await fetch(`/api/jobs/${encodeURIComponent(j.id)}/toggle`, {
      method: "POST",
    });
    // Resuming can be rejected by the active-jobs cap — surface it on the row
    // instead of silently leaving the job paused. Nothing changed server-side,
    // so skip the list refresh (which would wipe the transient message).
    if (!res.ok) {
      if (wasPaused) {
        const data = await res.json().catch(() => ({}));
        j._toggleErr =
          data.error || "Couldn't resume — max active jobs reached.";
        setTimeout(() => {
          j._toggleErr = null;
        }, 3200);
      }
      return;
    }
  } catch {
    return;
  }
  loadJobs();
}

async function runJobNow(j) {
  if (j.running) return;
  // Optimistic: pulse the row green immediately — the 1.5 s refresh confirms
  // the real state from the server (and reverts if the run couldn't start).
  j.running = true;
  try {
    await fetch(`/api/jobs/${encodeURIComponent(j.id)}/run`, {
      method: "POST",
    });
    window.__trackAppInsightsEvent?.("jobs.runNow", { jobId: j.id });
  } catch {}
  // Open the run's conversation as soon as its session exists, then keep the
  // transcript fresh until the run lands — so the user can see the session
  // instead of having to hunt for the row.
  watchJobRunAndOpen(j.id);
}

// After a run is triggered, the job's dedicated session is created within a few
// seconds. Poll the jobs list until it appears, open that conversation once,
// then refresh its transcript (background runs don't stream to the chat SSE)
// until the run finishes — so the run's answer shows up on its own. Stops if
// the user navigates to a different conversation (never yanks them back).
async function watchJobRunAndOpen(jobId) {
  let opened = false;
  for (let i = 0; i < 10; i++) {
    await new Promise((r) => setTimeout(r, i === 0 ? 700 : 1500));
    await loadJobs();
    const fresh = jobs.value.find((x) => x.id === jobId);
    if (!fresh || !fresh.sessionId) continue;
    if (!opened) {
      await selectSession(fresh.sessionId);
      loadSessions();
      opened = true;
    } else if (currentSessionId.value === fresh.sessionId) {
      await reloadSessionTranscript(fresh.sessionId);
    } else {
      return; // user moved to another conversation — stop refreshing
    }
    if (!fresh.running) return; // run finished — transcript now has the answer
  }
}

async function deleteJob(j) {
  // Immediate delete by design — no confirm dialog, and OPTIMISTIC: the row
  // disappears right away. If the server call fails, the next loadJobs()
  // refresh brings it back (rare) — far better than a × that appears dead.
  jobs.value = jobs.value.filter((x) => x.id !== j.id);
  try {
    const res = await fetch(`/api/jobs/${encodeURIComponent(j.id)}`, {
      method: "DELETE",
    });
    if (!res.ok && res.status !== 404)
      window.__trackAppInsightsEvent?.("jobs.deleteFailed", {
        status: String(res.status),
      });
  } catch {}
  loadJobs();
}

function openJob(j) {
  if (j.sessionId) {
    selectSession(j.sessionId);
    return;
  }
  // Never ran yet — there's no conversation to open, so show the job's details
  // in the edit sheet. A row click must always do something visible; the old
  // inline nudge was too subtle and read as "nothing happens".
  editJob(j);
}

function jobCadenceLabel(j) {
  const m = j.intervalMinutes;
  switch (m) {
    case 15:
      return "15 min";
    case 60:
      return "hourly";
    case 1440:
      return "daily";
    case 10080:
      return "weekly";
  }
  // Custom cadence — format to the largest whole unit for readability.
  if (m % 1440 === 0) return `every ${m / 1440} d`;
  if (m % 60 === 0) return `every ${m / 60} h`;
  return `every ${m} min`;
}

// "in 4 min" / "in 3 h" / "now" — counterpart of formatRelativeTime for
// FUTURE timestamps (next scheduled run). Reads nowTick so Vue re-renders
// countdowns every 30 s while the pane is open.
function formatUntil(iso) {
  if (!iso) return "";
  const diffMs = new Date(iso).getTime() - nowTick.value;
  if (diffMs <= 30000) return "any moment";
  const min = Math.round(diffMs / 60000);
  if (min < 60) return `in ${min} min`;
  const h = Math.round(min / 60);
  if (h < 48) return `in ${h} h`;
  return `in ${Math.round(h / 24)} d`;
}

// "2m ago" for PAST timestamps — like formatRelativeTime but reads nowTick so
// the job rows' "ran X ago" keeps ticking while the pane is open.
function formatAgo(iso) {
  if (!iso) return "";
  const diff = nowTick.value - new Date(iso).getTime();
  const min = Math.round(diff / 60000);
  if (min < 1) return "just now";
  if (min < 60) return `${min}m ago`;
  const h = Math.round(min / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.round(h / 24)}d ago`;
}

// Hover tooltip for a job row: last result (or the prompt) + run count.
function jobTooltip(j) {
  const base = j.lastSummary || j.prompt || "";
  return j.runCount
    ? `${base}\n\n${j.runCount} run${j.runCount === 1 ? "" : "s"} so far`
    : base;
}

async function newSession() {
  if (clearing.value) return;
  // Note: we do NOT abort an in-flight stream here. If another session is
  // running in the background, let it keep running — the SSE handlers will
  // detect that the user has navigated away and stop writing to the UI.
  clearing.value = true;
  messages.value = [];
  streamBuffer.value = "";
  // Drop only THIS view's live buckets (current session + the "__pending__"
  // sentinel) — a session still streaming in the background needs its bucket
  // intact so its tool list is whole when the user switches back.
  perSessionToolCalls.delete(currentSessionId.value || "__pending__");
  perSessionCharts.delete(currentSessionId.value || "__pending__");
  perSessionToolCalls.delete("__pending__");
  perSessionCharts.delete("__pending__");
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
  chartResizeObservers.forEach((ro) => {
    try {
      ro.disconnect();
    } catch {}
  });
  chartResizeObservers.length = 0;
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
  await reloadSessionTranscript(sessionId);
}

// Fetch the persisted transcript for a session and replay it into the view
// (messages, tool calls, charts, maturity scores). Unlike selectSession this
// does NOT guard against the already-current session — the background-tab
// recovery path reloads the CURRENTLY-active session after a severed stream.
async function reloadSessionTranscript(sessionId) {
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
  streamReasoning.value = "";
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
      const restored = (j.messages || []).map((m) => {
        // Re-derive the follow-up buttons from the persisted SuggestFollowUp
        // tool result — live turns keep them on the committed message, so a
        // restored transcript should too (last successful call wins).
        let followUp = null;
        for (const tc of m.toolCalls || []) {
          if (tc.name === "SuggestFollowUp" && tc.result) {
            try {
              const fu = JSON.parse(tc.result);
              if (fu.label && fu.prompt) followUp = fu;
            } catch {}
          }
        }
        return {
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
          followUp,
        };
      });
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

  // Land on the newest message — instantly. The messages.length watch
  // alone missed same-length transcript swaps and smooth-crawled across
  // long transcripts when switching conversations.
  forceScrollToBottom();

  // Re-attach: if this session has a turn still running SERVER-side (page was
  // refreshed mid-answer, or the SSE died while the CLI kept working), poll
  // until it finishes and then reload the transcript — instead of leaving the
  // user staring at a silent conversation that "doesn't respond".
  attachToServerTurn(sessionId);
}

// One poller at a time; superseded by any newer session selection.
let serverTurnPollToken = 0;
async function attachToServerTurn(sessionId) {
  const token = ++serverTurnPollToken;
  try {
    const r = await fetch(
      `/api/sessions/${encodeURIComponent(sessionId)}/active`,
    );
    if (!r.ok) return;
    const { active } = await r.json();
    if (!active || token !== serverTurnPollToken) return;
    // Show a lightweight working notice while the server-side turn finishes.
    messages.value = [
      ...messages.value,
      {
        role: "system",
        content:
          "⏳ Still working on your last question — the answer will appear here when ready.",
      },
    ];
    while (token === serverTurnPollToken) {
      await new Promise((res) => setTimeout(res, 4000));
      const p = await fetch(
        `/api/sessions/${encodeURIComponent(sessionId)}/active`,
      );
      if (!p.ok) return;
      const { active: still } = await p.json();
      if (!still) {
        if (token !== serverTurnPollToken) return;
        // Turn finished — drop the notice and reload the persisted transcript.
        messages.value = messages.value.filter(
          (m) => !(m.role === "system" && String(m.content).startsWith("⏳")),
        );
        await selectSession(sessionId);
        return;
      }
    }
  } catch {}
}

async function deleteSession(sessionId) {
  // Immediate delete by design — no confirm dialog, and OPTIMISTIC: the row
  // leaves the list before the server round-trip so the × always feels alive.
  sessions.value = sessions.value.filter((s) => s.id !== sessionId);
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
  try {
    const res = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}`, {
      method: "DELETE",
    });
    if (!res.ok && res.status !== 404)
      window.__trackAppInsightsEvent?.("sessions.deleteFailed", {
        status: String(res.status),
      });
  } catch {}
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
        // The backend always chats with its configured deployment and ignores
        // the request's model field — track the advertised id for display only.
        if (ids.length > 0) selectedModel.value = ids[0];
      }
    }
  } catch {}
}

// Pre-warm the Copilot session as soon as we know the user's identity, so the
// first prompt skips server-side session creation (system-prompt + tool-schema
// upload, ~300 ms) instead of paying it on the critical path. Fire-and-forget;
// any failure is harmless — the first /api/chat just creates the session then.
let warmedUp = false;
function warmUpSession() {
  if (warmedUp) return;
  warmedUp = true;
  fetch("/api/chat/warmup", {
    method: "POST",
    credentials: "same-origin",
  }).catch(() => {
    warmedUp = false; // allow a later retry if the warm-up call itself failed
  });
}

// Handle user login/logout
watch(
  () => props.user,
  (newUser, oldUser) => {
    if (newUser && !oldUser) {
      // User just logged in — fetch models and Azure status
      fetchModels();
      checkAzureStatus();
      warmUpSession();
    } else if (!newUser && oldUser) {
      // User logged out — clear all UI state
      clearMessages();
      warmedUp = false;
      azureConnected.value = false;
      azureUserEmail.value = "";
      azureSubscriptions.value = [];
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
  document.addEventListener("visibilitychange", onVisibilityChange);
  window.addEventListener("focus", onWindowFocus);
  // Sticky-scroll wiring: manual scrolls toggle following; ResizeObservers
  // keep the view pinned through ANY height change — content growth
  // (thinking panel, streamed text, charts) via .messages-inner, and
  // viewport shrink (composer autogrow, deck/script cards) via .messages.
  messagesEl.value?.addEventListener("scroll", onMessagesScroll, {
    passive: true,
  });
  if (typeof ResizeObserver !== "undefined" && messagesEl.value) {
    messagesResizeObserver = new ResizeObserver(() => scrollToBottom());
    messagesResizeObserver.observe(messagesEl.value);
    const inner = messagesEl.value.querySelector(".messages-inner");
    if (inner) messagesResizeObserver.observe(inner);
  }
  // Delegated handler for model-marked prompt chips ([label](prompt:...) links
  // rendered by renderContent). Delegation survives v-html re-renders.
  document.addEventListener("click", (e) => {
    const chip = e.target.closest?.(".prompt-chip");
    if (!chip) return;
    const q = chip.getAttribute("data-prompt");
    if (q && !streaming.value && !clearing.value) sendPrompt(q);
  });
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
      cooler._timer = setTimeout(
        () => {
          const arr = perSessionCoolers.get(sid) || [];
          perSessionCoolers.set(
            sid,
            arr.filter((x) => x._uid !== cooler._uid),
          );
        },
        wait * 1000 + 500,
      );
      list.push(cooler);
      perSessionCoolers.set(sid, [...list]);
      // eslint-disable-next-line no-console
      console.log(
        "[simulateCool] injected ghost row for",
        wait,
        "s in session",
        sid,
      );
      return cooler._uid;
    };
  }
  // Load saved conversations once we know whether the user is Azure-connected.
  // (The auth check fires on mount too; loadSessions is a no-op until then.)
  setTimeout(() => {
    loadSessions();
  }, 500);
  // Jobs pane liveness: tick the countdown clock every 30 s, and poll job
  // statuses every 45 s while the tab is visible — so a job that ran in the
  // background flips to "ran Xm ago / next in Y" without any user action.
  jobsTickTimer = setInterval(() => {
    nowTick.value = Date.now();
  }, 30000);
  jobsPollTimer = setInterval(() => {
    if (azureConnected.value && document.visibilityState === "visible")
      loadJobs();
  }, 45000);
  // Restore the last conversation after a full page reload/navigation — the
  // server keeps executing turns while the page is gone, so coming back should
  // always show the conversation (and pick up an answer that finished, or poll
  // for one still running). Works for anonymous users too via sessionStorage;
  // Entra users additionally get currentSessionId from /api/sessions.
  setTimeout(async () => {
    try {
      let sid = currentSessionId.value;
      if (!sid) {
        try {
          sid = sessionStorage.getItem("finops_last_session") || null;
        } catch {}
        if (!sid) return;
        const r = await fetch(
          `/api/sessions/${encodeURIComponent(sid)}/messages`,
        );
        if (!r.ok) {
          try {
            sessionStorage.removeItem("finops_last_session");
          } catch {}
          return;
        }
        const msgs = (await r.json()).messages || [];
        if (!msgs.length) return;
        currentSessionId.value = sid;
      }
      if (messages.value.length === 0 && !streaming.value) {
        await reloadSessionTranscript(sid);
        window.__trackAppInsightsEvent?.("chat.session.restored", {
          sessionId: sid,
        });
        // Page was reloaded mid-turn: the transcript ends with a user message
        // and no answer yet (the reload severed the SSE but the server kept
        // going). Poll the persisted transcript until the answer lands.
        const restored = messages.value;
        const last = restored[restored.length - 1];
        const lastUser = [...restored].reverse().find((m) => m.role === "user");
        if (
          last &&
          (last.role === "user" || last.role === "system") &&
          lastUser
        ) {
          tryRecoverPersistedAnswer(sid, String(lastUser.content || ""));
        }
      }
    } catch {}
  }, 1200);
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
    warmUpSession();
    await Promise.all([fetchModels(), checkAzureStatus()]);
  }
  // Clear auth loading overlay now that status is resolved
  if (pendingAuth) {
    sessionStorage.removeItem("authLoading");
    authLoading.value = "";
  }
});

let abortController = null;
// Background-tab recovery state. Browsers freeze/minimize background tabs, which
// suspends the in-flight SSE fetch. The backend never aborts the turn on client
// disconnect — it keeps generating and persists the answer to disk (see
// ChatEndpoints RequestAborted handler) — so on return we reconcile against the
// server's persisted transcript. We deliberately never abort a healthy stream
// on a client timer: reasoning models are legitimately silent for many seconds
// (time-to-first-token, long tool calls), so any "no bytes for N ms" rule would
// kill good answers.
let hiddenDuringStream = false; // a turn was in flight when we lost foreground
let reconcilingSid = null; // session id currently being reconciled (dedupe)
let reconcileAbort = false; // true when WE aborted a dead stream to force recovery
let inFlightTurn = null; // { sid, prompt } while a send() is running
let zombieProbeToken = 0; // cancels stale zombie probes

// Persist the active conversation id across full page reloads (same browser
// session). Entra users can re-find conversations via the sidebar, but for
// anonymous users currentSessionId lives only in JS memory — without this a
// reload orphans a conversation whose turn is still running server-side.
watch(currentSessionId, (id) => {
  try {
    if (id) sessionStorage.setItem("finops_last_session", id);
  } catch {}
});

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
  streamBuffer.value = "";
  streamIntent.value = "";
  streamReasoning.value = "";
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
  chartResizeObservers.forEach((ro) => {
    try {
      ro.disconnect();
    } catch {}
  });
  chartResizeObservers.length = 0;
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
  if (
    tc.done &&
    typeof tc.result === "string" &&
    tc.result.startsWith("HTTP 429")
  ) {
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
  return htmlCardIsReport(count)
    ? "FinOps report ready"
    : "FinOps presentation ready";
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
      chartResizeObservers.push(ro);
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
  chartResizeObservers.forEach((ro) => {
    try {
      ro.disconnect();
    } catch {}
  });
  chartResizeObservers.length = 0;
  if (jobsTickTimer) clearInterval(jobsTickTimer);
  if (jobsPollTimer) clearInterval(jobsPollTimer);
  document.removeEventListener("click", dismissPopover);
  document.removeEventListener("visibilitychange", onVisibilityChange);
  window.removeEventListener("focus", onWindowFocus);
  messagesEl.value?.removeEventListener("scroll", onMessagesScroll);
  messagesResizeObserver?.disconnect();
  messagesResizeObserver = null;
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

// ── Sticky auto-scroll ──────────────────────────────────────────────
// Follow new content ONLY while the user is at (or near) the bottom.
// Scrolling up to read pauses following (no more mid-read yanking);
// scrolling back down — or sending a message — resumes it. A
// ResizeObserver on the content column catches EVERY height change:
// streamed text, the "Thinking" reasoning panel appearing/growing, the
// intent ticker, charts mounting asynchronously, tables rendering, and
// the viewport shrinking (composer autogrow, deck/script cards).
// Watching streamBuffer alone missed all of those.
const stickToBottom = ref(true);
const AUTOSCROLL_SLACK_PX = 96;
// Grace window so a programmatic smooth scroll passing through
// not-at-bottom positions doesn't unstick us mid-animation.
let suppressUnstickUntil = 0;
let messagesResizeObserver = null;

function onMessagesScroll() {
  const el = messagesEl.value;
  if (!el) return;
  const nearBottom =
    el.scrollHeight - el.scrollTop - el.clientHeight <= AUTOSCROLL_SLACK_PX;
  if (nearBottom) {
    stickToBottom.value = true;
  } else if (performance.now() >= suppressUnstickUntil) {
    stickToBottom.value = false;
  }
}

// Sticky follow — instant, and a no-op when the user has scrolled up.
// Instant (not smooth): follow fires on every content growth tick, and
// restarting a smooth animation each tick lags behind the bottom.
function scrollToBottom() {
  if (!stickToBottom.value) return;
  nextTick(() => {
    const el = messagesEl.value;
    if (el && stickToBottom.value) {
      el.scrollTo({ top: el.scrollHeight, behavior: "instant" });
    }
  });
}

// Deliberate jump — user actions (send, session switch, "jump to
// latest") always land at the bottom and re-arm following. Smooth for
// short hops; instant when far away (a multi-screen smooth crawl is
// dizzying) or when repainting a whole transcript.
function forceScrollToBottom(smooth = false) {
  stickToBottom.value = true;
  nextTick(() => {
    const el = messagesEl.value;
    if (!el) return;
    const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
    // Smooth only for short hops in a VISIBLE document — smooth scrolling
    // is animation-frame driven and never completes in hidden/embedded
    // views, and a multi-screen smooth crawl is dizzying anyway.
    const useSmooth =
      smooth && distance < 2000 && document.visibilityState === "visible";
    suppressUnstickUntil = performance.now() + (useSmooth ? 800 : 150);
    el.scrollTo({
      top: el.scrollHeight,
      behavior: useSmooth ? "smooth" : "instant",
    });
  });
}

watch(() => messages.value.length, scrollToBottom);
// Reactive follow for stream-driven growth. These fire from Vue's
// reactivity (microtasks), so they work even in hidden/background tabs
// where ResizeObserver and rAF delivery are suspended. The RO still
// covers layout-only growth (charts mounting, images, composer resize).
watch(streamBuffer, scrollToBottom);
watch(streamReasoning, scrollToBottom);
watch(streamIntent, scrollToBottom);

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
      // Cells arrive pre-escaped — renderContent escapes the whole text
      // before any transform. Re-escaping here would double-encode &.
      (h, c) => `<th class="${numericColumns[c] ? "wt-num" : ""}">${h}</th>`,
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
  // NOTE: `raw` is already HTML-escaped — renderContent escapes the whole
  // text up front. Do NOT re-escape here (it would double-encode &amp;).
  const safe = raw;
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
  if (upMatch) return `<span class="wt-up">▲ ${upMatch[1]}</span>`;
  const downMatch = trimmed.match(/^(?:▼|↓|-)\s*([\d.,]+\s*%?)$/);
  if (downMatch) return `<span class="wt-down">▼ ${downMatch[1]}</span>`;
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
  // SECURITY: escape ALL HTML entities up front. Model output can carry
  // attacker-influenced content (fetched web pages, uploaded file contents,
  // Azure resource names/tags) — raw markup must never reach v-html. CSP
  // blocks script execution, but injected anchors/layout would still be a
  // phishing/defacement vector. Every transform below operates on the
  // escaped text; downstream helpers (buildWowTable/enhanceCell) must NOT
  // re-escape.
  // The & escape preserves entities the model already wrote (&lt; &#65; …):
  // entities in text nodes only ever decode to characters, never elements,
  // so passing them through is safe and keeps display fidelity.
  let html = String(text)
    .replace(/&(?!(?:[a-zA-Z][a-zA-Z0-9]*|#\d+|#x[\da-fA-F]+);)/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
  html = html.replace(
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
  // Clickable prompt chips: the model marks suggested questions with
  // [label](prompt:full question). Render as a chip; a delegated click
  // handler sends the question. Runs AFTER table building — the global
  // escape above only touches &<> so the [label](prompt:...) syntax
  // survives into table cells and is transformed here. Deterministic — no
  // guessing which text is clickable. Works in tables, lists, and prose.
  // label/q are already entity-escaped; only quotes need escaping for the
  // attribute value (getAttribute decodes entities back to the original).
  html = html.replace(
    /\[([^\]]+)\]\(prompt:([^)]+)\)/g,
    (_, label, q) =>
      `<button type="button" class="prompt-chip" data-prompt="${q.trim().replace(/"/g, "&quot;")}">${label.trim()}</button>`,
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

// Transient "Copied!" feedback on the script copy buttons — keyed by fileId
// so the checkmark shows on the exact button that was clicked.
const copiedScriptId = ref(null);
let copiedResetTimer = null;
function copyScript(script) {
  const content = typeof script === "string" ? script : script?.content || "";
  const id = (typeof script === "object" && script?.fileId) || "inline";
  navigator.clipboard
    .writeText(content)
    .then(() => {
      copiedScriptId.value = id;
      clearTimeout(copiedResetTimer);
      copiedResetTimer = setTimeout(() => {
        copiedScriptId.value = null;
      }, 1500);
    })
    .catch(() => {});
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
  // Image attachments are consumed by THIS message — the backend attaches
  // them as vision input and delists them after send. Mirror that here so
  // the chips don't linger and get "re-sent" visually on the next turn.
  // Data files (CSV/XLSX/…) stay: they remain queryable in later turns.
  const consumedImages = attachments.value.filter(isImageAttachment);
  if (consumedImages.length) {
    attachments.value = attachments.value.filter((a) => !isImageAttachment(a));
    for (const img of consumedImages) {
      if (img.thumbUrl) {
        try {
          URL.revokeObjectURL(img.thumbUrl);
        } catch {}
      }
    }
  }
  nextTick(() => {
    if (inputEl.value) inputEl.value.style.height = "auto";
  });
  // Track this stream's session id locally so we can detect (a) when the
  // user has navigated to a different session mid-stream, and (b) which
  // entry to drop from runningSessions when we finish.
  let streamingId = startSessionId || "__pending__";
  runningSessions.add(streamingId);
  // Register the in-flight turn for the zombie probe + recovery paths, and
  // invalidate any probe watching a previous turn.
  inFlightTurn = { sid: streamingId, prompt };
  zombieProbeToken++;
  const isActiveView = () =>
    streamingId === (currentSessionId.value || "__pending__");
  streamBuffer.value = "";
  activeTools.value = [];
  forceScrollToBottom(true);

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
  // Snapshot of the most recent tool_start narration wipe. If the turn ends
  // and NOTHING streamed after the last tool (i.e. the "narration" we wiped
  // was actually the final answer — e.g. answer text → late tool call → end),
  // commit restores this so the answer can never be lost from the view.
  let lastWipedText = "";
  // Artifacts produced by THIS stream. Kept stream-local (not in the shared
  // htmlReady/scriptReady refs) so a deck/script finishing in a background
  // session can't pop into whichever conversation is currently in view —
  // the shared refs are only mirrored while this session IS the view.
  let streamHtml = null;
  let streamScript = null;

  // === TIMING HOOKS ===
  // Captures every meaningful moment of the turn so we can build a flat
  // timing table afterwards. Records: client send, each SSE event arrival
  // (with type/tool), backend phase timings (token.*, session.*, sdk.*,
  // chat.total), tool durations from tool_done.durationMs, and 429 retries
  // from cooling_down. Dumped to console.table + window.__lastTimings
  // when the stream ends.
  const t0 = performance.now();
  const timings = [];
  let watchdogFired = false;
  const recordTiming = (entry) => {
    timings.push({ t_ms: Math.round(performance.now() - t0), ...entry });
  };
  recordTiming({
    kind: "client",
    phase: "send.start",
    prompt: prompt.slice(0, 60),
  });
  window.__trackAppInsightsEvent?.("chat.stream.start", {
    sessionId: currentSessionId.value || "__pending__",
    model: selectedModel.value || "",
    promptLen: String(prompt.length),
    attachments: String(attachments.value.length + consumedImages.length),
  });

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

    // Inactivity watchdog: if the server restarts mid-turn (deploy, crash),
    // the TCP socket can stay half-open and reader.read() hangs forever —
    // leaving a stuck blinking cursor. No event for 3 minutes (longest quiet
    // stretch of a single LLM round-trip is well under that) ⇒ abort and
    // surface the connection-lost recovery message.
    const WATCHDOG_MS = 180000;
    let watchdogTimer = null;
    const armWatchdog = () => {
      clearTimeout(watchdogTimer);
      watchdogTimer = setTimeout(() => {
        watchdogFired = true;
        try {
          abortController?.abort();
        } catch {}
      }, WATCHDOG_MS);
    };
    armWatchdog();

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      armWatchdog();

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
          recordTiming({
            kind: "server",
            phase: data.phase,
            server_ms: data.ms,
            extra: data.extra,
          });
        } else if (data.type === "tool_done") {
          recordTiming({
            kind: "event",
            phase: "tool_done",
            tool: data.tool,
            durationMs: data.durationMs,
            success: data.success,
          });
        } else if (data.type === "tool_start") {
          recordTiming({ kind: "event", phase: "tool_start", tool: data.tool });
        } else if (data.type === "cooling_down") {
          // eslint-disable-next-line no-console
          console.log("[SSE cooling_down arrived]", data);
          recordTiming({
            kind: "event",
            phase: "cooling_down",
            attempt: data.attempt,
            waitSec: data.waitSeconds,
          });
        } else if (data.type === "delta") {
          if (!timings.some((x) => x.phase === "first_delta")) {
            recordTiming({ kind: "event", phase: "first_delta" });
            window.__trackAppInsightsEvent?.("chat.stream.firstDelta", {
              sessionId: streamingId,
              ms: String(Math.round(performance.now() - t0)),
            });
          }
        } else if (data.type !== "message" && data.type !== "ping") {
          recordTiming({ kind: "event", phase: data.type });
        }

        // Routing/sidebar events are handled regardless of which session the
        // user is currently viewing. Everything else is gated on isActiveView()
        // below so a backgrounded stream keeps draining bytes (the server
        // needs us to keep reading) without polluting the foreground view.
        const routingEvent =
          data.type === "session" ||
          data.type === "session_title" ||
          data.type === "timing";
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
                if (inFlightTurn) inFlightTurn.sid = data.id;
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
                  modified: new Date().toISOString(),
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
            streamReasoning.value = "";
            enqueueText(data.content);
            hasDeltas = true;
            break;

          case "reasoning":
            // Live "thinking" panel — accumulate the reasoning summary (multi-
            // row, newlines preserved) and keep a rolling tail so the panel
            // reads like the model's stream of thought without growing forever.
            if (data.content) {
              const merged = streamReasoning.value + data.content;
              streamReasoning.value =
                merged.length > 1500 ? "…" + merged.slice(-1500) : merged;
            }
            break;

          case "busy":
            // Server rejected this prompt because a turn is already running in
            // this session. The optimistic user bubble was never accepted —
            // remove it, put the text back in the composer for a easy retry,
            // and surface the notice. The earlier turn keeps going.
            if (isActiveView()) {
              for (let li = messages.value.length - 1; li >= 0; li--) {
                const m = messages.value[li];
                if (m.role === "user" && m.content === prompt) {
                  messages.value.splice(li, 1);
                  break;
                }
              }
              if (!input.value.trim()) input.value = prompt;
              messages.value.push({
                role: "system",
                content: `⏳ ${data.message}`,
              });
            }
            break;

          case "message":
            if (data.content && !hasDeltas) enqueueText(data.content);
            break;

          case "tool_start":
            // Text streamed BEFORE a tool call is usually mid-turn narration
            // ("I'm rerunning…") — not the final answer — so we wipe it and let
            // the real answer stream after the last tool completes. EXCEPT for
            // post-answer tools: the system prompt makes the model call
            // SuggestFollowUp (and often RenderChart/presentation tools) AFTER
            // the final answer text has fully streamed. Wiping on those deleted
            // the just-delivered answer — the user watched their table render
            // and then vanish, leaving only the follow-up chip (the persisted
            // transcript still had it, which is why reloads brought it back).
            {
              const postAnswerTools = new Set([
                "SuggestFollowUp",
                "ReportMaturityScore",
                "PublishFAQ",
              ]);
              if (hasDeltas && !postAnswerTools.has(data.tool)) {
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
                // Keep the wiped text — if the turn ends with NO further deltas
                // (the "narration" WAS the answer), commit restores it instead
                // of losing the answer.
                lastWipedText = streamBuffer.value;
                streamBuffer.value = "";
                hasDeltas = false;
              }
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
              existing._timer = setTimeout(
                () => {
                  const arr = perSessionCoolers.get(streamingId) || [];
                  perSessionCoolers.set(
                    streamingId,
                    arr.filter((x) => x._uid !== existing._uid),
                  );
                },
                (data.waitSeconds || 5) * 1000 + 500,
              );
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
              cooler._timer = setTimeout(
                () => {
                  const arr = perSessionCoolers.get(streamingId) || [];
                  perSessionCoolers.set(
                    streamingId,
                    arr.filter((x) => x._uid !== cooler._uid),
                  );
                },
                (data.waitSeconds || 5) * 1000 + 500,
              );
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
            streamHtml = {
              fileId: data.fileId,
              fileName: data.fileName,
              slideCount: data.slideCount,
              createdAt: new Date().toLocaleString(undefined, {
                dateStyle: "medium",
                timeStyle: "short",
              }),
            };
            // Mirror to the shared ref only for the foreground view — see
            // streamHtml declaration for why.
            if (isActiveView()) {
              htmlReady.value = streamHtml;
              scrollToBottom();
            }
            break;

          case "script_ready":
            streamScript = {
              fileId: data.fileId,
              fileName: data.fileName,
              lineCount: data.lineCount,
              language: data.language,
              description: data.description,
              content: data.content,
              expanded: false,
            };
            if (isActiveView()) {
              scriptReady.value = streamScript;
              scrollToBottom();
            }
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

    // Restore a wiped answer: if the last text on screen was wiped by a late
    // tool_start and no new deltas followed, the wiped text WAS the answer.
    if (!streamBuffer.value.trim() && lastWipedText.trim()) {
      console.log(
        "[commit restore] turn ended with empty buffer; restoring wiped text len=",
        lastWipedText.length,
      );
      streamBuffer.value = lastWipedText;
    }

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
    if (streamHtml) {
      msgObj.html = { ...streamHtml };
      if (isActiveView()) htmlReady.value = null;
    }
    if (streamScript) {
      msgObj.script = { ...streamScript };
      if (isActiveView()) scriptReady.value = null;
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
    window.__trackAppInsightsEvent?.("chat.stream.done", {
      sessionId: streamingId,
      elapsedMs: String(Math.round(performance.now() - t0)),
      answerLen: String(clean.length),
      toolCount: String(toolCalls.length),
      committedToView: String(isActiveView()),
      hadDeltas: String(hasDeltas),
    });
  } catch (err) {
    // Was this abort OUR zombie-recovery (frozen background tab) rather than
    // the user pressing Stop? If so, fall through to the recovery path.
    const wasReconcileAbort = reconcileAbort;
    reconcileAbort = false;
    if (err.name === "AbortError" && !watchdogFired && !wasReconcileAbort) {
      // User pressed Stop (or the view was reset). Keep any partial text.
      window.__trackAppInsightsEvent?.("chat.stream.stopped", {
        sessionId: streamingId,
        elapsedMs: String(Math.round(performance.now() - t0)),
        partialLen: String(streamBuffer.value.length),
      });
      if (streamBuffer.value && isActiveView()) {
        messages.value.push({
          role: "assistant",
          content: streamBuffer.value + "\n\n*(generation stopped)*",
          toolCalls: toolCalls.map((tc) => ({ ...tc, expanded: false })),
          charts: [...charts],
        });
      }
    } else if (isActiveView()) {
      window.__trackAppInsightsEvent?.("chat.stream.severed", {
        sessionId: streamingId,
        elapsedMs: String(Math.round(performance.now() - t0)),
        watchdogFired: String(watchdogFired),
        reconcileAbort: String(wasReconcileAbort),
        errName: err?.name || "",
        partialLen: String(streamBuffer.value.length),
      });
      // A severed SSE stream usually means the app restarted mid-turn (deploy,
      // scale event, crash) OR the tab was backgrounded and the browser froze /
      // dropped the fetch. In every case the CLI keeps working and persists the
      // answer (SendAsync runs with no cancellation token), so recover it from
      // the server. Drop this session from runningSessions FIRST so the
      // transcript reload keeps (doesn't discard) the persisted trailing answer.
      runningSessions.delete(streamingId);
      // Fire-and-forget: the poller watches the persisted transcript for up to
      // the backend's full 15-min turn budget and converges the view itself —
      // either the recovered answer, or an actionable "Connection lost — Retry"
      // if the turn never lands. NOT awaited, so send()'s finally can reset the
      // composer immediately and a brand-new turn isn't blocked behind a long
      // poll (a mega-turn like "score every region × subscription" persists
      // nothing until the very end, so this can legitimately run for minutes).
      tryRecoverPersistedAnswer(streamingId, prompt);
    }
  } finally {
    clearInterval(intentAnimTimer);
    intentAnimTimer = null;
    runningSessions.delete(streamingId);
    if (inFlightTurn && inFlightTurn.sid === streamingId) inFlightTurn = null;

    // === TIMING DUMP ===
    // Stash on window for easy copy-paste; print a flat table to the
    // devtools console so we can eyeball where the time went.
    recordTiming({ kind: "client", phase: "send.end" });
    try {
      window.__lastTimings = timings;
      window.__lastTimingsPrompt = prompt;
      // eslint-disable-next-line no-console
      console.groupCollapsed(
        `[timing] ${prompt.slice(0, 60)} — ${timings.length} events, total ${timings[timings.length - 1]?.t_ms ?? 0}ms`,
      );
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
    for (const c of lingering) {
      if (c._timer) clearTimeout(c._timer);
    }
    perSessionCoolers.delete(streamingId);
    if (isActiveView()) {
      flushText();
      streamBuffer.value = "";
      activeTools.value = [];
      streamFollowUp.value = null;
      streamIntent.value = "";
      streamReasoning.value = "";
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
.attachment-chip-thumb {
  width: 22px;
  height: 22px;
  border-radius: 4px;
  object-fit: cover;
  flex-shrink: 0;
  border: 1px solid var(--border, #e2e8f0);
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
  /* No scroll-behavior:smooth here — scrolling behavior is controlled
     per-call in scrollToBottom/forceScrollToBottom (instant follow while
     streaming; smooth only for short deliberate hops). */
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

/* Floating "jump to latest" pill — anchored to the bottom edge of the
   messages viewport via a zero-height wrapper, so it tracks the composer
   regardless of its height (autogrow textarea, attachment chips, cards). */
.jump-latest-anchor {
  position: relative;
  height: 0;
  z-index: 6;
}
.jump-latest {
  position: absolute;
  bottom: 14px;
  left: 50%;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 14px;
  border: 1px solid rgba(15, 23, 42, 0.12);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.92);
  backdrop-filter: blur(8px);
  color: #1f2328;
  font-size: 0.78rem;
  font-weight: 600;
  font-family: inherit;
  cursor: pointer;
  box-shadow: 0 4px 16px rgba(15, 23, 42, 0.14);
  transition:
    background 0.15s ease,
    box-shadow 0.15s ease;
}
.jump-latest:hover {
  background: #fff;
  box-shadow: 0 6px 20px rgba(15, 23, 42, 0.2);
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
.message-row--system {
  justify-content: center;
}
.system-notice {
  max-width: 72%;
  margin: 2px 0;
  padding: 6px 14px;
  border-radius: 12px;
  background: #f3f2f1;
  border: 1px solid #e1dfdd;
  color: #605e5c;
  font-size: 12.5px;
  line-height: 1.45;
  text-align: center;
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
.stream-reasoning-block {
  margin: 6px 0 2px;
  padding: 10px 14px;
  background: linear-gradient(180deg, #fafbfc, #f3f5f7);
  border: 1px solid #e8eaed;
  border-radius: 10px;
  animation: intent-in 0.3s ease-out;
  max-width: 720px;
}
/* Model-marked clickable prompt suggestions — [label](prompt:...) links. */
:deep(.prompt-chip) {
  display: inline-block;
  margin: 2px 4px 2px 0;
  padding: 5px 12px;
  font-size: 13px;
  font-weight: 500;
  font-family: inherit;
  color: #0f6cbd;
  background: #f0f6fc;
  border: 1px solid #cfe4f7;
  border-radius: 14px;
  cursor: pointer;
  transition:
    background 0.15s ease,
    border-color 0.15s ease,
    transform 0.1s ease;
}
:deep(.prompt-chip:hover) {
  background: #e1effa;
  border-color: #9ecbf0;
  transform: translateY(-1px);
}
:deep(.prompt-chip:active) {
  transform: translateY(0);
}
.reasoning-label {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: #7a7d85;
  margin-bottom: 6px;
}
.reasoning-body {
  font-size: 13px;
  line-height: 1.55;
  color: #8a8d94;
  font-style: italic;
  word-break: break-word;
  /* Rolling window: cap ~6 rows, keep the newest text pinned visible. */
  max-height: 126px;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  justify-content: flex-end;
}
/* Markdown inside the muted "Thinking" panel. The reasoning stream is rendered
   through the same (HTML-escaping) renderContent as the main answer, but the
   main-answer styles are scoped to .message-text and don't reach here, so these
   compact + muted overrides keep headings/lists/code/tables from blowing up the
   small rolling-window box. The v-html output is wrapped in .reasoning-md so the
   flex container above still pins the newest lines to the bottom. */
.reasoning-md :deep(h2),
.reasoning-md :deep(h3),
.reasoning-md :deep(h4) {
  font-size: 13px;
  font-weight: 700;
  font-style: normal;
  margin: 4px 0 2px;
  color: #6b6e76;
}
.reasoning-md :deep(strong) {
  font-weight: 700;
  color: #6b6e76;
}
.reasoning-md :deep(ul) {
  margin: 2px 0;
  padding-left: 18px;
}
.reasoning-md :deep(li) {
  margin: 1px 0;
}
.reasoning-md :deep(code) {
  font-style: normal;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.05);
  padding: 0 4px;
  border-radius: 4px;
}
.reasoning-md :deep(pre) {
  font-style: normal;
  background: rgba(0, 0, 0, 0.04);
  border: 1px solid #e8eaed;
  border-radius: 6px;
  padding: 6px 8px;
  margin: 4px 0;
  overflow-x: auto;
  white-space: pre;
}
.reasoning-md :deep(pre code) {
  background: none;
  padding: 0;
}
/* A stray table in reasoning shouldn't dominate the panel — render it compact. */
.reasoning-md :deep(table) {
  font-size: 11px;
  border-collapse: collapse;
}
.reasoning-md :deep(.prompt-chip) {
  font-size: 12px;
  padding: 3px 9px;
}
/* Animated thinking dots — three staggered pulsing orbs. */
.thinking-dots {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}
.thinking-dots i {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #1f2328;
  animation: dot-bounce 1.2s ease-in-out infinite;
}
.thinking-dots i:nth-child(2) {
  animation-delay: 0.15s;
}
.thinking-dots i:nth-child(3) {
  animation-delay: 0.3s;
}
.thinking-dots--lg i {
  width: 9px;
  height: 9px;
}
@keyframes dot-bounce {
  0%,
  60%,
  100% {
    transform: translateY(0) scale(1);
    opacity: 0.45;
  }
  30% {
    transform: translateY(-5px) scale(1.15);
    opacity: 1;
  }
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
  /* Auto-grow: the textarea height is driven by autoGrowInput() setting an
     inline height from scrollHeight. It MUST NOT be a flex-grow item — inside
     the column .input-wrapper, `flex: 1` (flex-basis:0) makes the flex layout
     ignore the inline height and collapse the field to min-height, so multi-line
     input never expanded. `flex: 0 0 auto` lets the inline height take effect;
     full width still comes from the wrapper's default align-items: stretch. */
  flex: 0 0 auto;
  width: 100%;
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
  box-sizing: border-box;
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
  flex-shrink: 0; /* headers stay pinned while the pane's scroll area shrinks */
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
/* ── Right sidebar split: Agent (top) + Conversations + Jobs ──
   Each pane is a 2-row grid (header / content). The LAYOUT swap is instant
   (animating pane sizes makes sibling panes reflow every frame = jumping),
   but the revealed content slides + fades in — transform/opacity only, so
   it's buttery with zero layout work. Collapse hides immediately. */
.tools-sidebar-pane {
  display: grid;
  grid-template-rows: auto 1fr;
  min-height: 0;
}
.tools-sidebar-pane > .tools-sidebar-scroll {
  min-height: 0;
  opacity: 1;
  transform: translateY(0);
  transition:
    opacity 0.22s ease,
    transform 0.22s ease;
}
.tools-sidebar-pane--collapsed > .tools-sidebar-scroll {
  opacity: 0;
  transform: translateY(-8px);
  transition: none; /* swap OUT with the snap; swap IN animates */
}
.tools-sidebar-pane--collapsed > .tools-sidebar-scroll {
  /* the 0fr row removes the content; also remove the scroll padding so the
     pane lands exactly on its header height, no leftover sliver */
  padding-top: 0;
  padding-bottom: 0;
}
.tools-sidebar-pane--agent {
  flex: 1 1 auto;
  min-height: 120px;
}
.tools-sidebar-pane--sessions {
  flex: 1 1 auto;
  min-height: 140px;
  border-top: 1px solid #e1dfdd;
  background: #fff;
}
/* Collapsed pane = just its header; the content row glides to zero and the
   freed space flows to whichever pane(s) remain expanded — collapse two and
   the third gets everything. */
.tools-sidebar-pane--collapsed {
  grid-template-rows: auto 0fr;
  flex-grow: 0 !important;
  min-height: 0 !important;
  max-height: none !important;
}
.tools-sidebar-pane--collapsed .tools-sidebar-scroll {
  overflow: hidden;
}
/* Focus mode for Jobs: when both other panes are collapsed, lift the 42%
   cap so the jobs list can use the whole sidebar. */
.tools-sidebar:has(
    .tools-sidebar-pane--agent.tools-sidebar-pane--collapsed
  ):has(.tools-sidebar-pane--sessions.tools-sidebar-pane--collapsed)
  .tools-sidebar-pane--jobs:not(.tools-sidebar-pane--collapsed) {
  flex-grow: 1;
  max-height: none;
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
/* ── Scheduled jobs pane ── */
.tools-sidebar-pane--jobs {
  /* Sized by content up to 42% of the sidebar, but allowed to SHRINK when
     the window is short — the pane's own scroll area takes over instead of
     clipping. Grid rows (header/content) come from .tools-sidebar-pane. */
  flex: 0 1 auto;
  max-height: 42%;
  min-height: 36px; /* never less than the header — the pane stays reachable */
  border-top: 1px solid #e8eaed;
}
.jobs-scroll {
  overflow-y: auto;
  /* The jobs pane is the bottom-most pane — give the last row breathing room
     so it isn't cramped against the sidebar's bottom edge. */
  padding-bottom: 16px;
}
.jobs-header-toggle {
  cursor: pointer;
  user-select: none;
}
/* One glyph that rotates (▾ → ▸) — big, with a quick simple turn. */
.jobs-chevron {
  display: inline-block;
  font-size: 27px;
  line-height: 0.6;
  vertical-align: -4px;
  color: #57606a;
  margin-right: 7px;
  transform-origin: 42% 42%;
  transition:
    transform 0.15s ease,
    color 0.15s ease;
}
.jobs-chevron--collapsed {
  transform: rotate(-90deg);
}
.jobs-header-toggle:hover .jobs-chevron {
  color: #0f6cbd;
}
.jobs-attention {
  font-size: 11px;
  color: #cf222e;
  font-weight: 600;
  margin-left: 4px;
}
.jobs-header-toggle:focus-visible {
  outline: 2px solid #0f6cbd;
  outline-offset: 2px;
  border-radius: 6px;
}
.jobs-empty-cta {
  display: block;
  margin: 10px auto 2px;
  font: inherit;
  font-size: 12.5px;
  font-weight: 600;
  padding: 7px 16px;
  border: 1px solid #0f6cbd;
  border-radius: 8px;
  background: #fff;
  color: #0f6cbd;
  cursor: pointer;
  transition:
    background 0.12s ease,
    color 0.12s ease;
}
.jobs-empty-cta:hover {
  background: #0f6cbd;
  color: #fff;
}
/* Template picker — compact 2-col grid of prefill chips inside the form. */
.job-tpl-label {
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.4px;
  color: #8a8d94;
}
.job-tpl-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 5px;
}
.job-tpl {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  font: inherit;
  font-size: 11.5px;
  padding: 5px 8px;
  border: 1px solid #d7dade;
  border-radius: 7px;
  background: #fff;
  color: #333;
  cursor: pointer;
  transition:
    border-color 0.12s ease,
    background 0.12s ease;
}
.job-tpl:hover {
  border-color: #0f6cbd;
  background: #f3f8fc;
}
.job-tpl--selected {
  border-color: #0f6cbd;
  background: #e8f1fa;
  color: #0f6cbd;
  font-weight: 600;
}
.job-tpl-emoji {
  flex: 0 0 auto;
  font-size: 12px;
}
.job-tpl-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.job-row--paused .session-row-title {
  color: #9a9da4;
}
/* Alive: enabled + waiting for its next run — calm blue "breathing" glow so a
   returning user instantly sees the job is armed. Distinct from the green
   agent-pulse used while a run is actually executing. */
.job-dot--alive {
  background: #0078d4;
  animation: job-breathe 2.6s ease-in-out infinite;
}
@keyframes job-breathe {
  0%,
  100% {
    box-shadow: 0 0 0 0 rgba(0, 120, 212, 0.45);
    opacity: 1;
  }
  50% {
    box-shadow: 0 0 0 5px rgba(0, 120, 212, 0);
    opacity: 0.55;
  }
}
/* Dead: failed or auth-expired — urgent slow red pulse that draws the eye to
   the "reconnect Azure" hint without being frantic. */
.job-dot--dead {
  background: #cf222e;
  animation: job-dead-pulse 2s ease-in-out infinite;
}
@keyframes job-dead-pulse {
  0%,
  100% {
    box-shadow: 0 0 0 0 rgba(207, 34, 46, 0.4);
  }
  50% {
    box-shadow: 0 0 0 5px rgba(207, 34, 46, 0);
  }
}
.job-time--running {
  color: #1a7f37;
  font-weight: 600;
}
.job-time--dead {
  color: #cf222e;
  font-weight: 600;
}
/* ── Run-immediately checkbox in the create modal ── */
.job-runnow {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  font-size: 12.5px;
  color: #333;
  cursor: pointer;
  user-select: none;
}
.job-runnow input {
  margin-top: 2px;
  accent-color: #0f6cbd;
  cursor: pointer;
}
.job-runnow-hint {
  color: #8a8d94;
}
.job-row-btn {
  flex: 0 0 auto;
  border: none;
  background: none;
  font-size: 11px;
  line-height: 1;
  color: #7a7d85;
  cursor: pointer;
  padding: 4px;
  border-radius: 5px;
  transition: background 0.12s ease;
}
.job-row-btn:hover {
  background: #e3e6ea;
  color: #1f2328;
}
.job-row-btn:disabled {
  opacity: 0.35;
  cursor: default;
}
/* ── Job row actions — a hover OVERLAY on the right edge, not layout: the
   buttons used to permanently reserve ~120px of a ~225px row (opacity-0 but
   still in flow), squeezing the title + meta to a third of the row. Now the
   text owns the full width; actions fade in over the right edge on
   hover/focus with a gradient backdrop so covered text reads as intentional. ── */
.job-actions {
  position: absolute;
  top: 1px;
  bottom: 1px;
  right: 1px;
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 0 6px 0 22px;
  border-radius: 0 6px 6px 0;
  background: linear-gradient(
    to right,
    rgba(245, 246, 247, 0),
    var(--job-row-bg, #f5f6f7) 24px
  );
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.12s ease;
}
.session-row.job-row:hover .job-actions,
.session-row.job-row:focus-within .job-actions {
  opacity: 1;
  pointer-events: auto;
}
.session-row--current.job-row {
  --job-row-bg: #f3f2f1;
}
.job-actions .session-row-delete {
  opacity: 1; /* container controls reveal — no double fade */
}
/* ── Schedule on/off switch — replaces the ambiguous ⏸/⟳ icon (▶ next to ⏸
   read as contradictory transport controls; a switch reads as "schedule
   armed: yes/no"). Always visible on a PAUSED row (it's the state
   indicator); hover-revealed on armed rows like the other actions. ── */
.job-switch {
  flex: 0 0 auto;
  width: 26px;
  height: 15px;
  border-radius: 999px;
  border: 1px solid #cfd3d8;
  background: #e4e7ea;
  padding: 0;
  position: relative;
  cursor: pointer;
  transition:
    background 0.15s ease,
    border-color 0.15s ease;
}
.job-switch--on {
  background: #0f6cbd;
  border-color: #0f6cbd;
}
.job-switch-knob {
  position: absolute;
  top: 1px;
  left: 1px;
  width: 11px;
  height: 11px;
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.25);
  transition: transform 0.15s ease;
}
.job-switch--on .job-switch-knob {
  transform: translateX(11px);
}
.job-form {
  padding: 10px 12px;
  border-bottom: 1px solid #eceef1;
  display: flex;
  flex-direction: column;
  gap: 7px;
}
.job-form-input,
.job-form-prompt,
.job-form-select {
  width: 100%;
  box-sizing: border-box;
  font: inherit;
  font-size: 12.5px;
  padding: 7px 9px;
  border: 1px solid #d7dade;
  border-radius: 7px;
  background: #fff;
  color: #1f2328;
}
.job-form-prompt {
  resize: vertical;
  min-height: 58px;
}
.job-form-input:focus,
.job-form-prompt:focus,
.job-form-select:focus {
  outline: none;
  border-color: #0f6cbd;
}
.job-form-actions {
  display: flex;
  gap: 8px;
}
.job-form-create {
  font: inherit;
  font-size: 12.5px;
  font-weight: 600;
  padding: 6px 14px;
  border: none;
  border-radius: 7px;
  background: #0f6cbd;
  color: #fff;
  cursor: pointer;
}
.job-form-create:disabled {
  opacity: 0.5;
  cursor: default;
}
.job-form-cancel {
  font: inherit;
  font-size: 12.5px;
  padding: 6px 12px;
  border: 1px solid #d7dade;
  border-radius: 7px;
  background: #fff;
  color: #57606a;
  cursor: pointer;
}
.job-form-error {
  font-size: 12px;
  color: #cf222e;
}
.job-form-hint {
  font-size: 11.5px;
  line-height: 1.45;
  color: #8a8d94;
}
/* ── New job modal sheet ── */
.job-modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 3000;
  background: rgba(15, 23, 42, 0.42);
  backdrop-filter: blur(3px);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  animation: job-overlay-in 0.15s ease;
}
@keyframes job-overlay-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}
.job-modal {
  width: min(620px, 100%);
  max-height: min(86vh, 760px);
  display: flex;
  flex-direction: column;
  background: #fff;
  border-radius: 16px;
  box-shadow:
    0 24px 80px rgba(15, 23, 42, 0.35),
    0 4px 16px rgba(15, 23, 42, 0.18);
  overflow: hidden;
  animation: job-modal-in 0.18s ease-out;
}
@keyframes job-modal-in {
  from {
    opacity: 0;
    transform: translateY(8px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}
.job-modal-header {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  padding: 18px 20px 14px;
  border-bottom: 1px solid #eceef1;
  background: linear-gradient(180deg, #f7fafd, #fff);
}
.job-modal-icon {
  flex: 0 0 auto;
  width: 38px;
  height: 38px;
  border-radius: 10px;
  background: #e8f1fa;
  color: #0f6cbd;
  font-size: 20px;
  display: flex;
  align-items: center;
  justify-content: center;
}
.job-modal-heading {
  min-width: 0;
  flex: 1;
}
.job-modal-title {
  font-size: 16px;
  font-weight: 700;
  color: #1f2328;
}
.job-modal-subtitle {
  font-size: 12.5px;
  line-height: 1.45;
  color: #6b7280;
  margin-top: 2px;
}
.job-modal-close {
  flex: 0 0 auto;
  border: none;
  background: none;
  font-size: 22px;
  line-height: 1;
  color: #8a8d94;
  cursor: pointer;
  padding: 4px 8px;
  border-radius: 8px;
  transition:
    background 0.12s ease,
    color 0.12s ease;
}
.job-modal-close:hover {
  background: #eceef1;
  color: #1f2328;
}
.job-modal-body {
  padding: 16px 20px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  overflow-y: auto;
}
.job-modal-body > * {
  /* children keep their natural/auto-grown height — the BODY scrolls when
     content exceeds the sheet, instead of flex squashing the textarea */
  flex-shrink: 0;
}
.job-modal .job-tpl-grid {
  grid-template-columns: repeat(3, 1fr);
  margin-bottom: 6px;
}
.job-modal .job-tpl {
  padding: 8px 10px;
  font-size: 12px;
  border-radius: 9px;
}
.job-modal .job-tpl-label {
  margin-top: 6px;
}
.job-modal .job-form-prompt {
  /* auto-grown by autosizeJobPrompt() — never shows an inner scrollbar */
  resize: none;
  overflow-y: hidden;
}
.job-freq-row {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}
.job-freq {
  font: inherit;
  font-size: 12.5px;
  font-weight: 600;
  padding: 6px 14px;
  border: 1px solid #d7dade;
  border-radius: 999px;
  background: #fff;
  color: #57606a;
  cursor: pointer;
  transition:
    border-color 0.12s ease,
    background 0.12s ease,
    color 0.12s ease;
}
.job-freq:hover {
  border-color: #0f6cbd;
  color: #0f6cbd;
}
.job-freq--selected {
  border-color: #0f6cbd;
  background: #0f6cbd;
  color: #fff;
}
.job-freq--selected:hover {
  color: #fff;
}
.job-freq-expiry {
  font-size: 11.5px;
  color: #8a8d94;
  margin-left: 4px;
}
.job-freq-custom {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-top: 8px;
}
.job-freq-custom-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
  color: #57606a;
}
.job-freq-custom-input {
  width: 68px;
  font: inherit;
  font-size: 12.5px;
  font-weight: 600;
  text-align: center;
  padding: 5px 8px;
  border: 1px solid #d7dade;
  border-radius: 8px;
  color: #24292f;
  background: #fff;
}
.job-freq-custom-input:focus {
  outline: none;
  border-color: #0f6cbd;
  box-shadow: 0 0 0 3px rgba(15, 108, 189, 0.12);
}
.job-modal-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 14px 20px;
  border-top: 1px solid #eceef1;
  background: #fafbfc;
}
.job-modal .job-form-actions {
  display: flex;
  gap: 8px;
}
.job-modal .job-form-create {
  padding: 8px 18px;
  border-radius: 9px;
  box-shadow: 0 2px 6px rgba(15, 108, 189, 0.3);
  transition:
    transform 0.12s ease,
    box-shadow 0.12s ease;
}
.job-modal .job-form-create:hover:not(:disabled) {
  transform: translateY(-1px);
  box-shadow: 0 4px 10px rgba(15, 108, 189, 0.35);
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
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
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
  0% {
    background-position: 0% 50%;
  }
  100% {
    background-position: 200% 50%;
  }
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
.script-copy-btn--copied,
.script-copy-btn--copied:hover {
  background: #dff6dd;
  color: #107c10;
}
.artifact-expired {
  font-size: 12px;
  color: #605e5c;
  font-style: italic;
  white-space: nowrap;
  align-self: center;
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
