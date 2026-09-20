package com.stemcode.ui

import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.JBColor
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.components.JBLabel
import com.intellij.util.ui.JBUI
import com.intellij.util.ui.components.BorderLayoutPanel
import com.stemcode.StemCodePlugin
import com.stemcode.acp.*
import com.stemcode.services.*
import java.awt.*
import java.awt.event.*
import java.io.StringReader
import javax.swing.*
import javax.swing.text.*
import javax.swing.text.html.HTMLDocument
import javax.swing.text.html.HTMLEditorKit

/**
 * Rich chat panel for interacting with StemCode.
 *
 * Replaces the basic Swing chat with a feature-rich UI matching
 * the VS Code extension's webview capabilities:
 * - Message streaming (user, assistant, reasoning, system, tool)
 * - Tool call visualization with status
 * - Plan tracking with step progress
 * - Settings page with model, profile, thinking, provider config
 * - Slash command suggestions
 * - Modal dialogs for permissions and text input
 * - Model picker
 * - Profile selector
 * - File reference linking
 * - Progress indicator
 */
class ChatPanel(private val project: Project) : BorderLayoutPanel() {

    /**
     * Public accessor for the session manager, used by editor actions.
     */
    fun getSessionManager(): SessionManager = sessionManager

    /**
     * Public method to send a text prompt programmatically (used by editor actions).
     */
    fun sendTextPrompt(text: String) {
        if (!connected || isRunning) return
        val escaped = text.replace("\\", "\\\\").replace("\"", "\\\"")
        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                sessionManager.sendPrompt(text).get()
            } catch (e: Exception) {
                logger.warn("Failed to send prompt from editor action", e)
                SwingUtilities.invokeLater {
                    appendSystemMessage("Error: ${e.message}")
                }
            }
        }
    }

    private val logger = Logger.getInstance(ChatPanel::class.java)
    private val stemCodePlugin = StemCodePlugin.getInstance()
    private val processManager = stemCodePlugin.createProcessManager()
    private val sessionManager = SessionManager(processManager)
    private val logService = stemCodePlugin.getLogService()

    // UI Components
    private val chatHistory = JEditorPane()
    private val chatScrollPane = JBScrollPane(chatHistory)
    private val inputField = JTextPane()
    private val sendButton = JButton("\u2191")
    private val stopButton = JButton("Stop")
    private val connectButton = JButton("Start")
    private val statusLabel = JBLabel("Stopped")
    private val agentThreadLabel = JBLabel("No agent threads")
    private val modelButton = JButton("Model")
    private val profileComboBox = JComboBox<String>()
    private val settingsButton = JButton("\u2699")
    private val addContextButton = JButton("+")

    // State
    private var connected = false
    private var isRunning = false
    private var isCancelling = false
    private var currentSessionId: String? = null
    private var sessionInfo: AcpSessionInfo? = null
    private var activeAssistantMessage: MessageElement? = null
    private var activeReasoningMessage: MessageElement? = null
    private var hasPlanActivity = false

    // Message history and tool calls
    private val messagesPanel = BorderLayoutPanel()
    private val messageContainer = Box.createVerticalBox()
    private val toolCalls = mutableMapOf<String, ToolCallInfo>()
    private val toolMessageElements = mutableMapOf<String, MessageElement>()
    private val agentThreads = linkedMapOf<String, AgentThread>()
    private var activeAgentThreadId: String? = null
    private var currentPlan: List<AcpPlanEntry>? = null

    // Side pane for plan/tool activity
    private val sidePane = BorderLayoutPanel()
    private val planPanel: JPanel

    // Settings page
    private val settingsPanel = BorderLayoutPanel()
    private val settingsModelValue = JBLabel("-")
    private val settingsProfileValue = JBLabel("-")
    private val settingsThinkingValue = JBLabel("-")
    private val settingsProviderValue = JBLabel("-")
    private val settingsSummary = JBLabel("No active session.")

    // Suggestions
    private val suggestionsPopup = JPanel()
    private val suggestionsList = DefaultListModel<String>()
    private val suggestionsJList = JList(suggestionsList)

    // Modal dialog
    private var activeModal: JDialog? = null

    // Working directory
    private var workingDirectory: String = project.basePath ?: System.getProperty("user.dir")

    init {
        layout = BorderLayout()
        buildUI()
        registerSessionListeners()
        setConnectionUI(false)
        appendSystemMessage("Welcome to StemCode! Click Start to begin.")
    }

    private fun buildUI() {
        // ---- Chat History ----
        chatHistory.contentType = "text/html"
        chatHistory.isEditable = false
        chatHistory.background = JBColor.background()
        chatHistory.putClientProperty(JEditorPane.HONOR_DISPLAY_PROPERTIES, true)
        chatHistory.addHyperlinkListener { event ->
            if (event.eventType == javax.swing.event.HyperlinkEvent.EventType.ACTIVATED) {
                val desc = event.description
                if (desc != null) {
                    openFileFromChat(desc)
                }
            }
        }

        // ---- Message Container ----
        messageContainer.alignmentY = Component.TOP_ALIGNMENT
        messagesPanel.addToCenter(JBScrollPane(messageContainer).apply {
            border = JBUI.Borders.empty()
            horizontalScrollBarPolicy = JBScrollPane.HORIZONTAL_SCROLLBAR_NEVER
        })

        // ---- Input Area ----
        inputField.preferredSize = Dimension(Int.MAX_VALUE, 60)
        inputField.minimumSize = Dimension(0, 40)
        inputField.addKeyListener(object : KeyAdapter() {
            override fun keyPressed(e: KeyEvent) {
                if (!suggestionsPopup.isVisible && inputField.text.isBlank()) {
                    when (e.keyCode) {
                        KeyEvent.VK_UP -> {
                            if (switchAgentThread(-1)) {
                                e.consume()
                                return
                            }
                        }
                        KeyEvent.VK_DOWN -> {
                            if (switchAgentThread(1)) {
                                e.consume()
                                return
                            }
                        }
                    }
                }

                if (e.keyCode == KeyEvent.VK_ENTER && !e.isControlDown && !e.isShiftDown) {
                    e.consume()
                    sendMessage()
                }
                if (e.keyCode == KeyEvent.VK_ESCAPE) {
                    hideSuggestions()
                }
            }
        })
        inputField.addKeyListener(object : KeyAdapter() {
            override fun keyReleased(e: KeyEvent) {
                updateSuggestions()
            }
        })

        // ---- Send Button ----
        sendButton.isEnabled = false
        sendButton.addActionListener { sendMessage() }

        // ---- Stop Button ----
        stopButton.isEnabled = false
        stopButton.addActionListener { cancelPrompt() }

        // ---- Connect Button ----
        connectButton.addActionListener { toggleConnection() }

        // ---- Settings Button ----
        settingsButton.addActionListener { showSettingsPage() }
        settingsButton.toolTipText = "Settings"

        // ---- Add Context Button ----
        addContextButton.addActionListener {
            inputField.text = "/read "
            inputField.caretPosition = inputField.text.length
            inputField.requestFocus()
            updateSuggestions()
        }
        addContextButton.toolTipText = "Read workspace file"

        // ---- Model Button ----
        modelButton.addActionListener { showModelPicker() }

        // ---- Profile Combo ----
        profileComboBox.addActionListener {
            val selected = profileComboBox.selectedItem as? String
            if (selected != null && sessionInfo?.agentProfileName != selected) {
                sendSessionCommand("/profile $selected")
            }
        }

        // ---- Status Label ----
        statusLabel.border = JBUI.Borders.empty(4, 8)
        statusLabel.font = statusLabel.font.deriveFont(Font.ITALIC, 11f)
        agentThreadLabel.border = JBUI.Borders.empty(4, 0)
        agentThreadLabel.font = agentThreadLabel.font.deriveFont(11f)
        agentThreadLabel.foreground = JBColor.GRAY

        // ---- Top Toolbar ----
        val toolbar = JPanel(BorderLayout())
        val leftToolbar = JPanel(FlowLayout(FlowLayout.LEFT, 4, 2))
        leftToolbar.add(connectButton)
        leftToolbar.add(statusLabel)
        leftToolbar.add(agentThreadLabel)

        val centerToolbar = JPanel(FlowLayout(FlowLayout.CENTER, 4, 2))
        centerToolbar.add(modelButton)
        centerToolbar.add(profileComboBox)

        val rightToolbar = JPanel(FlowLayout(FlowLayout.RIGHT, 4, 2))
        rightToolbar.add(addContextButton)
        rightToolbar.add(settingsButton)

        toolbar.add(leftToolbar, BorderLayout.WEST)
        toolbar.add(centerToolbar, BorderLayout.CENTER)
        toolbar.add(rightToolbar, BorderLayout.EAST)

        // ---- Composer Area ----
        val composerPanel = BorderLayoutPanel()
        composerPanel.border = JBUI.Borders.empty(4)

        val inputRow = JPanel(BorderLayout())
        val inputScroll = JBScrollPane(inputField)
        inputScroll.preferredSize = Dimension(Int.MAX_VALUE, 60)
        inputScroll.border = JBUI.Borders.empty()
        inputRow.add(inputScroll, BorderLayout.CENTER)

        val buttonPanel = JPanel(FlowLayout(FlowLayout.RIGHT, 4, 0))
        buttonPanel.add(stopButton)
        buttonPanel.add(sendButton)
        inputRow.add(buttonPanel, BorderLayout.EAST)

        composerPanel.addToCenter(inputRow)

        // ---- Bottom toolbar (model/profile/status) ----
        val bottomToolbar = JPanel(BorderLayout())
        val bottomLeft = JPanel(FlowLayout(FlowLayout.LEFT, 8, 2))
        bottomLeft.add(JBLabel("Model:"))
        bottomLeft.add(modelButton)
        bottomLeft.add(JBLabel("Profile:"))
        bottomLeft.add(profileComboBox)

        val bottomRight = JPanel(FlowLayout(FlowLayout.RIGHT, 4, 2))
        bottomRight.add(stopButton)

        bottomToolbar.add(bottomLeft, BorderLayout.WEST)
        bottomToolbar.add(bottomRight, BorderLayout.EAST)

        // ---- Plan Side Pane ----
        planPanel = JPanel()
        planPanel.layout = BoxLayout(planPanel, BoxLayout.Y_AXIS)
        val planScroll = JBScrollPane(planPanel)
        planScroll.border = JBUI.Borders.empty()
        planScroll.preferredSize = Dimension(Int.MAX_VALUE, 120)

        sidePane.addToCenter(planScroll)
        sidePane.border = JBUI.Borders.empty(4, 0, 0, 0)

        // ---- Settings Page ----
        buildSettingsPage()

        // ---- Suggestions Popup ----
        suggestionsPopup.layout = BorderLayout()
        suggestionsJList.visibleRowCount = 6
        suggestionsJList.selectionMode = ListSelectionModel.SINGLE_SELECTION
        suggestionsJList.addMouseListener(object : MouseAdapter() {
            override fun mouseClicked(e: MouseEvent) {
                if (e.clickCount == 2) {
                    applySelectedSuggestion()
                }
            }
        })
        suggestionsJList.addKeyListener(object : KeyAdapter() {
            override fun keyPressed(e: KeyEvent) {
                if (e.keyCode == KeyEvent.VK_ENTER) {
                    applySelectedSuggestion()
                }
            }
        })
        val suggestionsScroll = JBScrollPane(suggestionsJList)
        suggestionsScroll.preferredSize = Dimension(300, 180)
        suggestionsPopup.add(suggestionsScroll, BorderLayout.CENTER)
        suggestionsPopup.isVisible = false

        // ---- Main Layout ----
        val mainPanel = BorderLayoutPanel()
        mainPanel.addToTop(toolbar)
        mainPanel.addToCenter(chatScrollPane)
        mainPanel.addToBottom(composerPanel)

        val tabbedPane = JTabbedPane()
        tabbedPane.addTab("Chat", mainPanel)
        tabbedPane.addTab("Settings", settingsPanel)

        addToTop(tabbedPane)

        // ---- Initial State ----
        updateComposerState()
    }

    private fun buildSettingsPage() {
        settingsPanel.layout = BorderLayout()

        val settingsContent = JPanel()
        settingsContent.layout = BoxLayout(settingsContent, BoxLayout.Y_AXIS)
        settingsContent.border = JBUI.Borders.empty(12)

        // Title
        val title = JBLabel("StemCode Settings")
        title.font = title.font.deriveFont(Font.BOLD, 18f)
        settingsContent.add(title)
        settingsContent.add(Box.createVerticalStrut(8))

        settingsContent.add(settingsSummary)
        settingsContent.add(Box.createVerticalStrut(16))

        // Session Group
        settingsContent.add(createSettingsGroup("Session", listOf(
            SettingsAction("Model", "Choose active model for this session.", settingsModelValue) { showModelPicker() },
            SettingsAction("Profile", "Switch build, plan, review, or subagent profile.", settingsProfileValue) {
                sendSessionCommand("/setting profile")
            },
            SettingsAction("Thinking", "Set provider reasoning effort.", settingsThinkingValue) {
                sendSessionCommand("/setting thinking")
            },
            SettingsAction("Summary", "Show provider, model, profile, thinking, and session details.") {
                sendSessionCommand("/setting summary")
            }
        )))
        settingsContent.add(Box.createVerticalStrut(12))

        // Provider Group
        settingsContent.add(createSettingsGroup("Provider", listOf(
            SettingsAction("Provider", "Switch saved provider for this session.", settingsProviderValue) {
                sendSessionCommand("/setting provider")
            },
            SettingsAction("Onboarding", "Add or repair provider credentials.") {
                sendSessionCommand("/setting onboarding")
            }
        )))
        settingsContent.add(Box.createVerticalStrut(12))

        // Workspace Group
        settingsContent.add(createSettingsGroup("Workspace", listOf(
            SettingsAction("Workspace Files", "Create or review .stemcode project files.") {
                sendSessionCommand("/setting workspace")
            },
            SettingsAction("Budget", "Configure local or cloud budget controls.") {
                sendSessionCommand("/setting budget")
            },
            SettingsAction("Permissions", "Edit modes, sandbox, and session overrides.") {
                sendSessionCommand("/setting permissions")
            },
            SettingsAction("Rules", "Inspect effective tool permission rules.") {
                sendSessionCommand("/setting rules")
            },
            SettingsAction("Tools", "Show MCP servers, custom tools, and dynamic tool status.") {
                sendSessionCommand("/setting tools")
            }
        )))
        settingsContent.add(Box.createVerticalStrut(12))

        // Extension Group
        settingsContent.add(createSettingsGroup("Extension", listOf(
            SettingsAction("Plugin Settings", "Edit command, args, working directory, auto-start, and log level.") {
                // Open IDE settings
            }
        )))

        val scrollPane = JBScrollPane(settingsContent)
        settingsPanel.addToCenter(scrollPane)
    }

    private fun createSettingsGroup(title: String, actions: List<SettingsAction>): JPanel {
        val group = JPanel()
        group.layout = BoxLayout(group, BoxLayout.Y_AXIS)
        group.border = JBUI.Borders.empty(0)

        val header = JBLabel(title)
        header.font = header.font.deriveFont(Font.BOLD, 12f)
        header.foreground = JBColor.GRAY
        group.add(header)
        group.add(Box.createVerticalStrut(6))

        actions.forEach { action ->
            val button = JButton()
            button.layout = BorderLayout()
            button.border = JBUI.Borders.empty(8)
            button.isContentAreaFilled = false
            button.isFocusPainted = false
            button.cursor = Cursor.getPredefinedCursor(Cursor.HAND_CURSOR)

            val textPanel = JPanel()
            textPanel.layout = BoxLayout(textPanel, BoxLayout.Y_AXIS)
            val titleLabel = JBLabel(action.title)
            titleLabel.font = titleLabel.font.deriveFont(Font.BOLD, 13f)
            textPanel.add(titleLabel)

            if (action.description != null) {
                val descLabel = JBLabel(action.description)
                descLabel.font = descLabel.font.deriveFont(11f)
                descLabel.foreground = JBColor.GRAY
                textPanel.add(descLabel)
            }

            button.add(textPanel, BorderLayout.CENTER)

            if (action.valueLabel != null) {
                action.valueLabel.font = action.valueLabel.font.deriveFont(11f)
                action.valueLabel.foreground = JBColor.GRAY
                button.add(action.valueLabel, BorderLayout.EAST)
            }

            button.addActionListener { action.onClick() }
            group.add(button)
            group.add(Box.createVerticalStrut(4))
        }

        return group
    }

    private data class SettingsAction(
        val title: String,
        val description: String? = null,
        val valueLabel: JBLabel? = null,
        val onClick: () -> Unit
    )

    // ---- Connection Management ----

    private fun toggleConnection() {
        if (connected) {
            disconnect()
        } else {
            connect()
        }
    }

    private fun connect() {
        connectButton.isEnabled = false
        connectButton.text = "Connecting..."

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val wd = project.basePath ?: System.getProperty("user.dir")
                processManager.start(wd).get()

                // Create session
                val client = processManager.getClient() ?: throw RuntimeException("Process started but no client")
                val sid = client.createSession(wd).get()

                SwingUtilities.invokeLater {
                    currentSessionId = sid
                    connected = true
                    setConnectionUI(true)
                    appendSystemMessage("Connected | Session: $sid")
                }
            } catch (e: Exception) {
                logger.warn("Failed to connect to StemCode", e)
                SwingUtilities.invokeLater {
                    setConnectionUI(false)
                    appendSystemMessage("Connection failed: ${e.message}")
                }
            }
        }
    }

    private fun disconnect() {
        val client = processManager.getClient()
        if (client != null) {
            ApplicationManager.getApplication().executeOnPooledThread {
                try {
                    currentSessionId?.let { client.closeSession(it).get() }
                } catch (_: Exception) {}
                processManager.stop().get()
            }
        }
        currentSessionId = null
        connected = false
        agentThreads.clear()
        activeAgentThreadId = null
        updateActiveAgentThreadLabel()
        setConnectionUI(false)
        appendSystemMessage("Disconnected")
    }

    private fun setConnectionUI(connected: Boolean) {
        connectButton.text = if (connected) "Disconnect" else "Start"
        sendButton.isEnabled = connected && !isRunning
        inputField.isEnabled = connected && !isRunning
        stopButton.isEnabled = isRunning
        statusLabel.text = when {
            isRunning -> if (isCancelling) "Cancelling" else "Running"
            connected -> "Ready"
            else -> "Stopped"
        }
        statusLabel.foreground = when {
            isRunning -> JBColor.YELLOW
            connected -> JBColor.GREEN
            else -> JBColor.RED
        }
    }

    // ---- Session Listeners ----

    private fun registerSessionListeners() {
        sessionManager.onProcessStatusChanged = { status ->
            SwingUtilities.invokeLater {
                statusLabel.text = status.name.lowercase().replaceFirstChar { it.uppercase() }
            }
        }

        sessionManager.onPromptStateChanged = { state ->
            SwingUtilities.invokeLater {
                isRunning = state.isRunning
                isCancelling = state.isCancelling
                if (state.isRunning) {
                    activeAssistantMessage = null
                    activeReasoningMessage = null
                }
                if (!state.isRunning && state.lastStopReason == "cancelled") {
                    appendSystemMessage("Cancelled.")
                }
                setConnectionUI(connected)
                updateComposerState()
            }
        }

        sessionManager.onSessionInfoChanged = { info ->
            SwingUtilities.invokeLater {
                sessionInfo = info
                updateSessionInfo()
            }
        }

        sessionManager.onMessageChunk = { chunk ->
            SwingUtilities.invokeLater {
                handleMessageChunk(chunk)
            }
        }

        sessionManager.onToolCallUpdate = { update ->
            SwingUtilities.invokeLater {
                val merged = toolCalls.getOrPut(update.toolCallId) { update }.copy(
                    status = update.status,
                    content = if (update.content.isNotEmpty()) update.content
                              else toolCalls[update.toolCallId]?.content ?: emptyList(),
                    rawInput = update.rawInput ?: toolCalls[update.toolCallId]?.rawInput,
                    title = update.title ?: toolCalls[update.toolCallId]?.title,
                    kind = update.kind ?: toolCalls[update.toolCallId]?.kind
                )
                toolCalls[update.toolCallId] = merged
                trackAgentThreads(merged)
                renderToolMessage(merged)
            }
        }

        sessionManager.onPlanUpdate = { entries ->
            SwingUtilities.invokeLater {
                currentPlan = entries
                hasPlanActivity = entries.isNotEmpty()
                renderPlan(entries)
            }
        }

        sessionManager.onClientRequest = { request ->
            SwingUtilities.invokeLater {
                showClientRequest(request)
            }
        }

        sessionManager.onClientRequestResolved = { id ->
            SwingUtilities.invokeLater {
                // Modal handled elsewhere
            }
        }
    }

    // ---- Message Handling ----

    private fun handleMessageChunk(chunk: SessionMessageChunk) {
        when (chunk.role) {
            "user" -> appendUserMessage(chunk.text)
            "reasoning" -> appendReasoningChunk(chunk.text)
            "assistant" -> appendAssistantChunk(chunk.text)
        }
    }

    private fun appendUserMessage(text: String) {
        val html = """
            <div style='margin:8px 0;text-align:right'>
                <div style='display:inline-block;background:#4a90d9;color:white;padding:8px 12px;border-radius:12px 12px 4px 12px;max-width:80%;text-align:left'>
                    <b>You</b><br>${htmlEscape(text)}
                </div>
            </div>
        """.trimIndent()
        appendToHistory(html)
        activeAssistantMessage = null
        activeReasoningMessage = null
    }

    private fun appendAssistantChunk(text: String) {
        if (activeAssistantMessage == null) {
            activeAssistantMessage = MessageElement(text)
        } else {
            activeAssistantMessage = activeAssistantMessage?.copy(text = (activeAssistantMessage?.text ?: "") + text)
        }
        activeReasoningMessage = null

        val fullText = activeAssistantMessage?.text ?: text
        val html = """
            <div style='margin:8px 0'>
                <div style='display:inline-block;background:#2d2d2d;color:#e0e0e0;padding:8px 12px;border-radius:12px 12px 12px 4px;max-width:80%'>
                    <b style='color:#4a90d9'>StemCode</b><br>${formatMessageText(fullText)}
                </div>
            </div>
        """.trimIndent()

        // Replace last assistant message or add new
        replaceOrAppendLast(html, "StemCode")
    }

    private fun appendReasoningChunk(text: String) {
        if (activeReasoningMessage == null) {
            activeReasoningMessage = MessageElement(text)
        } else {
            activeReasoningMessage = activeReasoningMessage?.copy(text = (activeReasoningMessage?.text ?: "") + text)
        }
        activeAssistantMessage = null

        val fullText = activeReasoningMessage?.text ?: text
        val html = """
            <div style='margin:6px 0'>
                <div style='display:inline-block;background:#242424;color:#a8a8a8;border-left:3px solid #4a90d9;padding:7px 10px;border-radius:6px;max-width:80%'>
                    <b style='color:#8ab4f8'>Thinking</b><br>${htmlEscape(fullText)}
                </div>
            </div>
        """.trimIndent()

        replaceOrAppendLast(html, "Thinking")
    }

    private fun appendSystemMessage(text: String) {
        val html = """
            <div style='margin:4px 0;text-align:center'>
                <span style='color:#888;font-size:0.9em;font-style:italic'>${htmlEscape(text)}</span>
            </div>
        """.trimIndent()
        appendToHistory(html)
    }

    private fun appendToHistory(html: String) {
        try {
            val doc = chatHistory.document as HTMLDocument
            val kit = chatHistory.editorKit as HTMLEditorKit
            val reader = StringReader("<div style='font-family:system-ui,sans-serif;font-size:14px'>$html</div>")
            kit.read(reader, doc, doc.length)
            SwingUtilities.invokeLater {
                chatHistory.caretPosition = doc.length
            }
        } catch (e: Exception) {
            logger.warn("Failed to append to chat history", e)
        }
    }

    private fun replaceOrAppendLast(html: String, identifier: String) {
        // Simple approach: remove last similar block by tracking message count
        // For simplicity, append and scroll
        appendToHistory(html)
    }

    // ---- Tool Call Rendering ----

    private fun renderToolMessage(call: ToolCallInfo) {
        val statusColor = when (call.status) {
            "completed" -> "#73c991"
            "failed" -> "#f48771"
            else -> "#cca700"
        }
        val output = call.content.joinToString("\n")
        val title = call.title ?: call.toolCallId

        val html = """
            <div style='margin:6px 0'>
                <div style='border-left:3px solid $statusColor;padding:7px 9px;border-radius:6px;background:#252526'>
                    <div style='color:$statusColor;font-size:10px;font-weight:bold;text-transform:uppercase'>${call.status}</div>
                    <div style='color:#e0e0e0;font-size:12px;font-weight:600;margin-top:2px'>${htmlEscape(title)}</div>
                    ${if (call.kind != null) "<div style='color:#888;font-size:11px'>${htmlEscape(call.kind)}</div>" else ""}
                    ${if (output.isNotBlank()) "<pre style='max-height:260px;margin:6px 0 0;padding:8px;overflow:auto;border:1px solid #333;border-radius:6px;color:#e0e0e0;background:#1e1e1e;font-size:12px;white-space:pre-wrap'>${htmlEscape(output)}</pre>" else "<div style='color:#888;font-size:11px;margin-top:4px'>Waiting for output...</div>"}
                </div>
            </div>
        """.trimIndent()

        appendToHistory(html)
    }

    // ---- Plan Rendering ----

    private fun renderPlan(entries: List<AcpPlanEntry>?) {
        planPanel.removeAll()

        if (entries == null || entries.isEmpty()) {
            planPanel.add(JBLabel("No active plan."))
            planPanel.revalidate()
            planPanel.repaint()
            return
        }

        entries.forEach { entry ->
            val dotColor = when (entry.status) {
                "completed" -> JBColor.GREEN
                "in_progress" -> JBColor.YELLOW
                else -> JBColor.GRAY
            }
            val itemPanel = JPanel(BorderLayout())
            itemPanel.border = JBUI.Borders.empty(4, 8)
            itemPanel.maximumSize = Dimension(Int.MAX_VALUE, 40)

            val dot = JPanel()
            dot.preferredSize = Dimension(8, 8)
            dot.background = dotColor
            dot.border = JBUI.Borders.empty()

            val label = JBLabel(entry.content)
            label.font = label.font.deriveFont(12f)
            label.border = JBUI.Borders.empty(0, 6, 0, 0)

            itemPanel.add(dot, BorderLayout.WEST)
            itemPanel.add(label, BorderLayout.CENTER)
            planPanel.add(itemPanel)
            planPanel.add(Box.createVerticalStrut(4))
        }

        planPanel.revalidate()
        planPanel.repaint()
    }

    private fun trackAgentThreads(call: ToolCallInfo) {
        createAgentThreads(call).forEach { thread ->
            agentThreads[thread.id] = thread
        }

        if (activeAgentThreadId == null || agentThreads[activeAgentThreadId] == null) {
            activeAgentThreadId = agentThreads.values.lastOrNull()?.id
        }

        updateActiveAgentThreadLabel()
    }

    private fun createAgentThreads(call: ToolCallInfo): List<AgentThread> {
        val rawInput = call.rawInput as? JsonElement ?: return emptyList()
        val rawObject = rawInput.asJsonObjectOrNull() ?: return emptyList()
        val output = call.content.joinToString("\n").trim()

        rawObject.getString("agent")?.takeIf { it.isNotBlank() }?.let { agent ->
            return listOf(AgentThread(
                id = call.toolCallId,
                toolCallId = call.toolCallId,
                agent = agent,
                task = rawObject.getString("task").orEmpty(),
                context = rawObject.getString("context").orEmpty(),
                ownership = "",
                status = call.status,
                output = output,
                source = "delegate"
            ))
        }

        val tasks = rawObject.get("tasks")?.takeIf { it.isJsonArray }?.asJsonArray ?: return emptyList()
        return tasks.mapIndexedNotNull { index, taskElement ->
            val taskObject = taskElement.asJsonObjectOrNull() ?: return@mapIndexedNotNull null
            val agent = taskObject.getString("agent")?.takeIf { it.isNotBlank() } ?: return@mapIndexedNotNull null
            AgentThread(
                id = "${call.toolCallId}:$index",
                toolCallId = call.toolCallId,
                agent = agent,
                task = taskObject.getString("task").orEmpty(),
                context = taskObject.getString("context").orEmpty(),
                ownership = taskObject.getString("ownership").orEmpty(),
                status = call.status,
                output = output,
                source = "orchestrate"
            )
        }
    }

    private fun switchAgentThread(delta: Int): Boolean {
        if (agentThreads.isEmpty()) {
            return false
        }

        val threads = agentThreads.values.toList()
        val currentIndex = threads.indexOfFirst { it.id == activeAgentThreadId }.let { if (it >= 0) it else 0 }
        val nextIndex = Math.floorMod(currentIndex + delta, threads.size)
        activeAgentThreadId = threads[nextIndex].id
        updateActiveAgentThreadLabel()
        return true
    }

    private fun updateActiveAgentThreadLabel() {
        val thread = activeAgentThreadId?.let(agentThreads::get)
        if (thread == null) {
            agentThreadLabel.text = "No agent threads"
            agentThreadLabel.toolTipText = null
            return
        }

        agentThreadLabel.text = "@${thread.agent} ${thread.status}"
        agentThreadLabel.toolTipText = buildString {
            append("@").append(thread.agent)
            append(" [").append(thread.status).append("]")
            if (thread.task.isNotBlank()) {
                append("\nTask: ").append(thread.task)
            }
            if (thread.context.isNotBlank()) {
                append("\nContext: ").append(thread.context)
            }
            if (thread.ownership.isNotBlank()) {
                append("\nOwnership: ").append(thread.ownership)
            }
            if (thread.output.isNotBlank()) {
                append("\n\n").append(thread.output.take(400))
                if (thread.output.length > 400) {
                    append("...")
                }
            }
            append("\n\nUse ArrowUp / ArrowDown on an empty composer to switch threads.")
        }
    }

    // ---- Message Sending ----

    private fun sendMessage() {
        val text = inputField.text.trim()
        if (text.isEmpty()) return
        if (!connected || isRunning) return

        inputField.text = ""
        sendButton.isEnabled = false
        appendUserMessage(text)

        if (text.startsWith("/")) {
            handleSlashCommand(text)
        } else {
            ApplicationManager.getApplication().executeOnPooledThread {
                try {
                    sessionManager.sendPrompt(text).get()
                } catch (e: Exception) {
                    logger.warn("Failed to send prompt", e)
                    SwingUtilities.invokeLater {
                        appendSystemMessage("Error: ${e.message}")
                        sendButton.isEnabled = connected
                    }
                }
            }
        }
    }

    private fun handleSlashCommand(text: String) {
        when {
            text == "/clear" -> {
                clearChat()
                appendSystemMessage("Screen cleared.")
            }
            text == "/models" -> showModelPicker()
            text.startsWith("/read ") -> handleReadFile(text.removePrefix("/read ").trim())
            text.startsWith("/profile ") -> {
                val profile = text.removePrefix("/profile ").trim()
                sendSessionCommand(text)
            }
            else -> {
                sendSessionCommand(text)
            }
        }
    }

    private fun sendSessionCommand(command: String) {
        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                sessionManager.sendPrompt(command).get()
            } catch (e: Exception) {
                logger.warn("Session command failed", e)
                SwingUtilities.invokeLater {
                    appendSystemMessage("Command failed: ${e.message}")
                }
            }
        }
    }

    private fun handleReadFile(requestedPath: String) {
        if (requestedPath.isBlank()) {
            appendSystemMessage("Usage: /read <file>")
            return
        }

        val root = project.basePath ?: return
        val normalizedPath = requestedPath.replace("\\", "/")
        val file = java.io.File(root, normalizedPath)

        if (!file.exists() || !file.canonicalPath.startsWith(root)) {
            appendSystemMessage("File not found or outside workspace: $requestedPath")
            return
        }

        try {
            val content = file.readText()
            appendSystemMessage("File: $requestedPath\n\n$content")
        } catch (e: Exception) {
            appendSystemMessage("Error reading file: ${e.message}")
        }
    }

    private fun cancelPrompt() {
        stopButton.isEnabled = false
        sessionManager.cancelPrompt()
    }

    private fun clearChat() {
        try {
            val doc = chatHistory.document as HTMLDocument
            doc.remove(0, doc.length)
        } catch (e: Exception) {}
        toolCalls.clear()
        toolMessageElements.clear()
        agentThreads.clear()
        activeAgentThreadId = null
        updateActiveAgentThreadLabel()
        activeAssistantMessage = null
        activeReasoningMessage = null
        currentPlan = null
        renderPlan(null)
    }

    // ---- Model Picker ----

    private fun showModelPicker() {
        val models = sessionInfo?.availableModelIds ?: emptyList()
        if (models.isEmpty()) {
            sendSessionCommand("/models")
            return
        }

        val activeModel = sessionInfo?.modelId ?: ""

        val dialog = JDialog(if (project.isOpen) project as? Window else null, "Choose Model", ModalityType.APPLICATION_MODAL)
        dialog.layout = BorderLayout()
        dialog.border = JBUI.Borders.empty(12)
        dialog.setSize(400, 500)
        dialog.setLocationRelativeTo(null)

        val panel = JPanel()
        panel.layout = BoxLayout(panel, BoxLayout.Y_AXIS)

        if (activeModel.isNotBlank()) {
            val current = JBLabel("Current: $activeModel")
            current.font = current.font.deriveFont(12f)
            current.foreground = JBColor.GRAY
            panel.add(current)
            panel.add(Box.createVerticalStrut(8))
        }

        models.forEach { model ->
            val button = JButton(model)
            button.alignmentX = Component.LEFT_ALIGNMENT
            button.isContentAreaFilled = false
            button.isFocusPainted = false
            button.cursor = Cursor.getPredefinedCursor(Cursor.HAND_CURSOR)
            button.addActionListener {
                if (model != activeModel) {
                    sendSessionCommand("/use $model")
                }
                dialog.dispose()
            }
            panel.add(button)
            panel.add(Box.createVerticalStrut(4))
        }

        val scroll = JBScrollPane(panel)
        dialog.add(scroll, BorderLayout.CENTER)

        val closeButton = JButton("Close")
        closeButton.addActionListener { dialog.dispose() }
        dialog.add(closeButton, BorderLayout.SOUTH)

        dialog.isVisible = true
    }

    // ---- Client Request Modal ----

    private fun showClientRequest(request: ClientRequest) {
        if (activeModal != null && activeModal!!.isVisible) return

        val dialog = JDialog(if (project.isOpen) project as? Window else null, "StemCode Request", ModalityType.APPLICATION_MODAL)
        dialog.layout = BorderLayout()
        dialog.border = JBUI.Borders.empty(12)
        dialog.setSize(500, 400)
        dialog.setLocationRelativeTo(null)
        dialog.defaultCloseOperation = WindowConstants.DO_NOTHING_ON_CLOSE

        val panel = JPanel()
        panel.layout = BoxLayout(panel, BoxLayout.Y_AXIS)

        val titleLabel = JBLabel(request.title ?: "StemCode request")
        titleLabel.font = titleLabel.font.deriveFont(Font.BOLD, 16f)
        panel.add(titleLabel)

        if (request.description != null) {
            panel.add(Box.createVerticalStrut(8))
            val descLabel = JBLabel(request.description)
            descLabel.font = descLabel.font.deriveFont(12f)
            panel.add(descLabel)
        }

        panel.add(Box.createVerticalStrut(16))

        if (request.kind == "text") {
            val textField = JPasswordField(30)
            textField.text = request.defaultValue ?: ""
            if (!request.isSecret) {
                textField.echoChar = '\u0000'
            }
            panel.add(textField)

            val submitButton = JButton("Submit")
            submitButton.addActionListener {
                val value = String(textField.password)
                sessionManager.resolveClientRequest(request.id, ClientRequestResolution.Submitted(value))
                dialog.dispose()
            }
            panel.add(submitButton)
        } else {
            request.options.forEach { option ->
                val button = JButton(option.name)
                button.alignmentX = Component.LEFT_ALIGNMENT
                button.addActionListener {
                    sessionManager.resolveClientRequest(request.id, ClientRequestResolution.Selected(option.optionId))
                    dialog.dispose()
                }
                panel.add(button)
                panel.add(Box.createVerticalStrut(4))
            }
        }

        if (request.allowCancellation) {
            val cancelButton = JButton("Cancel")
            cancelButton.addActionListener {
                sessionManager.resolveClientRequest(request.id, ClientRequestResolution.Cancelled)
                dialog.dispose()
            }
            panel.add(cancelButton)
        }

        val scroll = JBScrollPane(panel)
        dialog.add(scroll, BorderLayout.CENTER)

        activeModal = dialog
        dialog.isVisible = true
        activeModal = null
    }

    // ---- File Opening ----

    private fun openFileFromChat(filePath: String) {
        val root = project.basePath ?: return
        val file = java.io.File(root, filePath.trim().removePrefix("file://"))

        if (!file.exists()) {
            // Try to find the file in the project
            val vf = LocalFileSystem.getInstance().findFileByIoFile(file)
            if (vf != null) {
                openVirtualFile(vf)
                return
            }
            appendSystemMessage("Could not open file: $filePath")
            return
        }

        val vf = LocalFileSystem.getInstance().findFileByIoFile(file)
        if (vf != null) {
            openVirtualFile(vf)
        }
    }

    private fun openVirtualFile(vf: VirtualFile) {
        val openAction = Runnable {
            com.intellij.openapi.fileEditor.FileEditorManager.getInstance(project).openFile(vf, true)
        }
        ApplicationManager.getApplication().invokeLater(openAction)
    }

    // ---- Suggestions ----

    private val slashCommands = listOf(
        "/a" to "Alias for /agent.",
        "/allow <tool-or-tag> [pattern]" to "Add a session-scoped allow override.",
        "/agent" to "List available subagents.",
        "/budget [status|local [path]|cloud]" to "Show or configure budget controls.",
        "/clear" to "Clear the chat view.",
        "/clone" to "Duplicate the current session.",
        "/compact [retained-turns]" to "Manually compact the session context.",
        "/config" to "Show provider, profile, thinking, and model details.",
        "/context-size [show|auto|8k|32k|64k|125k|256k|<tokens>]" to "Show, set, or clear the session context window cap.",
        "/copy" to "Copy the last agent message to the clipboard.",
        "/deny <tool-or-tag> [pattern]" to "Add a session-scoped deny override.",
        "/exit" to "Exit the interactive shell.",
        "/export [json|html] [path]" to "Export the current session.",
        "/fork [turn-number]" to "Create a fork from a previous user message.",
        "/help" to "List available commands.",
        "/import <json-path>" to "Import a session from JSON.",
        "/init [recommended|minimal|custom]" to "Create workspace-local StemCode files.",
        "/ls" to "List files in the current workspace.",
        "/mcp" to "Show MCP servers and dynamic tools.",
        "/models" to "Open the active model picker.",
        "/new" to "Start a new session.",
        "/onboard" to "Re-run provider onboarding.",
        "/permissions" to "Show permission policy and overrides.",
        "/provider [list|<name>]" to "List or switch saved providers.",
        "/profile <name>" to "Switch the active agent profile.",
        "/read <file>" to "Read a workspace file after confirmation.",
        "/redo" to "Re-apply the most recently undone edit.",
        "/reload" to "Reload profiles, skills, prompts, and tools.",
        "/resume [session-id]" to "Resume a different session.",
        "/rules" to "List effective permission rules.",
        "/session" to "Show session info and stats.",
        "/setting [area]" to "Open configurable StemCode settings.",
        "/share" to "Share the current session as a secret GitHub gist.",
        "/thinking [on|off]" to "Show or set thinking mode.",
        "/tree" to "Navigate saved sessions and forks.",
        "/undo" to "Roll back the most recent tracked edit.",
        "/update [now]" to "Check for StemCode updates.",
        "/use <model>" to "Switch the active model directly."
    )

    private fun updateSuggestions() {
        val text = inputField.text
        if (!text.startsWith("/")) {
            suggestionsPopup.isVisible = false
            return
        }

        // Build matching suggestions
        suggestionsList.clear()
        val lower = text.lowercase()
        slashCommands.filter { (cmd, _) ->
            cmd.lowercase().startsWith(lower) || cmd.lowercase().contains(lower)
        }.forEach { (cmd, desc) ->
            suggestionsList.addElement("$cmd - $desc")
        }

        // Also match models for /use
        if (text.startsWith("/use ")) {
            val query = text.removePrefix("/use ").trim().lowercase()
            sessionInfo?.availableModelIds?.filter { it.lowercase().contains(query) }
                ?.forEach { suggestionsList.addElement("/use $it") }
        }

        // Also match profiles for /profile
        if (text.startsWith("/profile ")) {
            val query = text.removePrefix("/profile ").trim().lowercase()
            sessionInfo?.availableAgentProfiles?.filter { it.lowercase().contains(query) }
                ?.forEach { suggestionsList.addElement("/profile $it") }
        }

        // Also match built-in profiles
        if (text.startsWith("/profile ")) {
            val query = text.removePrefix("/profile ").trim().lowercase()
            listOf("build", "plan", "review", "explore", "general").filter { it.contains(query) }
                .forEach { suggestionsList.addElement("/profile $it") }
        }

        suggestionsPopup.isVisible = suggestionsList.size() > 0
    }

    private fun applySelectedSuggestion() {
        val selected = suggestionsJList.selectedValue ?: return
        val cmd = selected.substringBefore(" - ")
        inputField.text = cmd
        inputField.caretPosition = cmd.length
        hideSuggestions()
        inputField.requestFocus()
    }

    private fun hideSuggestions() {
        suggestionsPopup.isVisible = false
    }

    // ---- Helpers ----

    private fun updateComposerState() {
        sendButton.isEnabled = connected && !isRunning && inputField.text.isNotBlank()
        inputField.isEnabled = connected && !isRunning
        stopButton.isEnabled = isRunning
    }

    private fun updateSessionInfo() {
        val info = sessionInfo
        if (info != null) {
            settingsModelValue.text = info.modelId ?: "-"
            settingsProfileValue.text = info.agentProfileName ?: "-"
            settingsThinkingValue.text = info.thinkingMode ?: "default"
            settingsProviderValue.text = info.providerName ?: "-"
            settingsSummary.text = "${info.providerName ?: "No provider"} / ${info.modelId ?: "No model"} / ${info.agentProfileName ?: "No profile"} / thinking ${info.thinkingMode ?: "default"}"

            // Update model button
            modelButton.text = info.modelId ?: "Model"

            // Update profile combo
            val profiles = info.availableAgentProfiles ?: emptyList()
            if (profiles.isNotEmpty()) {
                profileComboBox.removeAllItems()
                profiles.forEach { profileComboBox.addItem(it) }
                val currentProfile = info.agentProfileName
                if (currentProfile != null) {
                    for (i in 0 until profileComboBox.itemCount) {
                        if (profileComboBox.getItemAt(i) == currentProfile) {
                            profileComboBox.selectedIndex = i
                            break
                        }
                    }
                }
            }
        }
    }

    private fun htmlEscape(text: String): String {
        return text
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace("\"", "&quot;")
            .replace("'", "&#39;")
            .replace("\n", "<br>")
    }

    private fun formatMessageText(text: String): String {
        // Escape HTML and convert file references to clickable links
        val escaped = htmlEscape(text)
        // Simple pattern for file:line:column references
        return escaped.replace(
            Regex("([\\w./\\\\-]+\\.\\w{1,15})(?::(\\d+))?(?::(\\d+))?")
        ) { matchResult ->
            val groups = matchResult.groupValues
            val fileRef = buildString {
                append(groups[1])
                if (groups[2].isNotEmpty()) {
                    append(":").append(groups[2])
                    if (groups[3].isNotEmpty()) {
                        append(":").append(groups[3])
                    }
                }
            }
            "<a href='${groups[1]}'>$fileRef</a>"
        }
    }

    private data class MessageElement(
        val text: String
    )

    private data class AgentThread(
        val id: String,
        val toolCallId: String,
        val agent: String,
        val task: String,
        val context: String,
        val ownership: String,
        val status: String,
        val output: String,
        val source: String
    )
}

private fun JsonElement.asJsonObjectOrNull(): JsonObject? =
    if (isJsonObject) asJsonObject else null

private fun JsonObject.getString(propertyName: String): String? =
    get(propertyName)
        ?.takeIf { it.isJsonPrimitive && it.asJsonPrimitive.isString }
        ?.asString
        ?.trim()
