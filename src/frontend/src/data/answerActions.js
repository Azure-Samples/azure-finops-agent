// What the buttons under the latest answer send. Make a deck builds from the conversation: its earlier fixed prompt
// ("where we're wasting money and the fixes the agent will run for us") asked for waste findings in conversations that
// held none, signed-out ones included, and promised fixes the agent never runs; it applies only approved changes and
// writes scripts for the user to review (session evaluation of 107 conversations, November 2026).
export const DECK_PROMPT =
  "Build a 6-slide deck for my FinOps team from this conversation: what we asked, what you found (only figures your tools returned) and the recommended next steps as proposals for us to review.";

export const SCRIPT_PROMPT =
  "Based on our conversation, generate an Azure CLI script to implement the FinOps recommendations we discussed. Ask me to confirm the specific actions before generating the script. If there are no actionable recommendations yet, let me know.";
