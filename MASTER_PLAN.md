# CBIT MSP Platform — Master Plan

## Project Overview

CBIT Inc. is building a custom PSA (Professional Services Automation) and RMM (Remote Monitoring and Management) platform to replace SyncroMSP. This is an internal-only tool for CBIT technicians — it will never be sold or licensed to other MSPs.

- **Domain:** axis.gocbit.com
- **Users:** 5 technicians + admins (2 permission groups: Techs and Admins)
- **Customers:** 280+ managed service customers
- **Hosting:** Self-hosted VM on TrueNAS array in CBIT's datacenter
- **Authentication:** Standalone OAuth2 (not integrated with apps.gocbit.com)
- **No invoicing or billing features of any kind**

---

## Tech Stack

| Component | Technology | Rationale |
|-----------|------------|-----------|
| Frontend | React (Vite) | Modern, component-based UI |
| Backend API | Node.js (Express or Fastify) | Team expertise, large ecosystem |
| Database | PostgreSQL | Relational data, strong querying for reports |
| RMM Agent | C# / .NET 8 Windows Service | Native Windows API access (WMI, Windows Update, Services) |
| Agent-to-Server | HTTPS REST + WebSocket | REST for check-ins, WebSocket for real-time terminal |
| Email Integration | Microsoft Graph API | Native O365 integration for support@gocbit.com |
| Authentication | OAuth2 (standalone) | Role-based access, JWT tokens |

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│                     axis.gocbit.com                        │
│                                                          │
│  ┌──────────────┐  ┌──────────────────┐  ┌────────────┐ │
│  │  React SPA   │  │  Node.js API     │  │ PostgreSQL │ │
│  │  (Frontend)  │──│  (Backend)       │──│ (Database)  │ │
│  └──────────────┘  └────────┬─────────┘  └────────────┘ │
│                             │                            │
│                      ┌──────┴───────┐                    │
│                      │  WebSocket   │                    │
│                      │  Server      │                    │
│                      └──────┬───────┘                    │
└─────────────────────────────┼────────────────────────────┘
                              │ HTTPS / WSS (Public Internet)
            ┌─────────────────┼─────────────────┐
            │                 │                 │
      ┌─────┴─────┐    ┌─────┴─────┐    ┌─────┴─────┐
      │  C# Agent │    │  C# Agent │    │  C# Agent │
      │ (Endpoint) │    │ (Endpoint) │    │ (Endpoint) │
      └───────────┘    └───────────┘    └───────────┘

External Services:
  - Microsoft Graph API (support@gocbit.com inbox)
  - ScreenConnect (https://cbit.screenconnect.com)
```

---

## User Roles and Permissions

Two groups only:

| Feature | Tech | Admin |
|---------|------|-------|
| View/manage own tickets | Yes | Yes |
| View/manage all tickets | Yes | Yes |
| Create/edit customers | Yes | Yes |
| Create/edit assets | Yes | Yes |
| Remote terminal | Yes | Yes |
| ScreenConnect access | Yes | Yes |
| Manage users and groups | No | Yes |
| System settings | No | Yes |
| Admin-configurable dropdowns | No | Yes |
| Worksheet templates | No | Yes |
| Update policies | No | Yes |
| Agent version management | No | Yes |
| Alert rule configuration | No | Yes |
| Notification settings (global) | No | Yes |
| Custom field definitions | No | Yes |

---

## Database Schema

### Customers

```sql
CREATE TABLE customers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  email VARCHAR(255),
  phone VARCHAR(50),
  address_line1 VARCHAR(255),
  address_line2 VARCHAR(255),
  city VARCHAR(100),
  state VARCHAR(50),
  zip VARCHAR(20),
  notes TEXT,
  network_summary TEXT,
  circuit_id VARCHAR(100),
  ip_address VARCHAR(50),
  subnet_mask VARCHAR(50),
  on_call_hours VARCHAR(100),
  management_plan VARCHAR(255),
  customer_type VARCHAR(100),
  microsoft_tenant VARCHAR(255),
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE contacts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id UUID NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
  first_name VARCHAR(100) NOT NULL,
  last_name VARCHAR(100) NOT NULL,
  email VARCHAR(255),
  phone VARCHAR(50),
  is_primary BOOLEAN DEFAULT false,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE custom_field_definitions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  field_name VARCHAR(255) NOT NULL,
  field_type VARCHAR(20) NOT NULL CHECK (field_type IN ('text', 'number', 'dropdown', 'boolean', 'textarea')),
  dropdown_options JSONB,
  display_order INTEGER DEFAULT 0,
  is_required BOOLEAN DEFAULT false,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE custom_field_values (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id UUID NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
  field_definition_id UUID NOT NULL REFERENCES custom_field_definitions(id) ON DELETE CASCADE,
  value TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW(),
  UNIQUE(customer_id, field_definition_id)
);
```

### Users and Auth

```sql
CREATE TABLE users (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  email VARCHAR(255) UNIQUE NOT NULL,
  password_hash VARCHAR(255) NOT NULL,
  display_name VARCHAR(100) NOT NULL,
  role VARCHAR(10) NOT NULL CHECK (role IN ('admin', 'tech')),
  email_signature TEXT,
  notification_preferences JSONB DEFAULT '{}',
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE refresh_tokens (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token VARCHAR(500) NOT NULL,
  expires_at TIMESTAMPTZ NOT NULL,
  created_at TIMESTAMPTZ DEFAULT NOW()
);
```

### Ticketing

```sql
CREATE TABLE ticket_statuses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(100) NOT NULL,
  is_default BOOLEAN DEFAULT false,
  is_closed BOOLEAN DEFAULT false,
  display_order INTEGER DEFAULT 0,
  color VARCHAR(7)
);

CREATE TABLE ticket_priorities (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(50) NOT NULL,
  level INTEGER NOT NULL,
  display_order INTEGER DEFAULT 0,
  color VARCHAR(7)
);

CREATE TABLE ticket_types (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(100) NOT NULL,
  display_order INTEGER DEFAULT 0,
  is_active BOOLEAN DEFAULT true
);

CREATE TABLE tickets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ticket_number SERIAL UNIQUE,
  subject VARCHAR(500) NOT NULL,
  customer_id UUID REFERENCES customers(id),
  contact_id UUID REFERENCES contacts(id),
  assigned_to UUID REFERENCES users(id),
  status_id UUID NOT NULL REFERENCES ticket_statuses(id),
  priority_id UUID REFERENCES ticket_priorities(id),
  type_id UUID REFERENCES ticket_types(id),
  parent_ticket_id UUID REFERENCES tickets(id),
  created_by_email VARCHAR(255),
  email_message_id VARCHAR(500),
  email_conversation_id VARCHAR(500),
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW(),
  resolved_at TIMESTAMPTZ
);

CREATE TABLE ticket_communications (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ticket_id UUID NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
  author_type VARCHAR(10) NOT NULL CHECK (author_type IN ('tech', 'customer', 'system')),
  author_id UUID REFERENCES users(id),
  author_name VARCHAR(100) NOT NULL,
  author_email VARCHAR(255),
  comm_type VARCHAR(15) NOT NULL CHECK (comm_type IN ('email', 'private_note', 'system_note')),
  body TEXT NOT NULL,
  email_message_id VARCHAR(500),
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE ticket_communication_attachments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  communication_id UUID NOT NULL REFERENCES ticket_communications(id) ON DELETE CASCADE,
  filename VARCHAR(255) NOT NULL,
  file_path VARCHAR(500) NOT NULL,
  file_size BIGINT,
  mime_type VARCHAR(100),
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE ticket_cc (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ticket_id UUID NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
  email VARCHAR(255) NOT NULL,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE ticket_assets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ticket_id UUID NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
  asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  created_at TIMESTAMPTZ DEFAULT NOW()
);
```

### Worksheets and Checklists

```sql
CREATE TABLE worksheet_templates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE worksheet_template_items (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  template_id UUID NOT NULL REFERENCES worksheet_templates(id) ON DELETE CASCADE,
  label VARCHAR(500) NOT NULL,
  item_type VARCHAR(15) NOT NULL CHECK (item_type IN ('checkbox', 'text_input')),
  is_required BOOLEAN DEFAULT false,
  display_order INTEGER DEFAULT 0,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE ticket_worksheets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ticket_id UUID NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
  template_id UUID NOT NULL REFERENCES worksheet_templates(id),
  is_finalized BOOLEAN DEFAULT false,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE ticket_worksheet_items (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ticket_worksheet_id UUID NOT NULL REFERENCES ticket_worksheets(id) ON DELETE CASCADE,
  template_item_id UUID NOT NULL REFERENCES worksheet_template_items(id),
  is_checked BOOLEAN DEFAULT false,
  text_value TEXT,
  completed_by UUID REFERENCES users(id),
  completed_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ DEFAULT NOW()
);
```

### Assets and RMM

```sql
CREATE TABLE assets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id UUID NOT NULL REFERENCES customers(id),
  agent_id VARCHAR(255) UNIQUE NOT NULL,
  hostname VARCHAR(255),
  display_name VARCHAR(255),
  os_type VARCHAR(20) CHECK (os_type IN ('windows_workstation', 'windows_server')),
  os_version VARCHAR(100),
  os_build VARCHAR(50),
  manufacturer VARCHAR(255),
  model VARCHAR(255),
  serial_number VARCHAR(100),
  cpu_model VARCHAR(255),
  cpu_cores INTEGER,
  ram_gb DECIMAL(10,2),
  domain VARCHAR(255),
  last_user VARCHAR(255),
  wan_ip VARCHAR(50),
  last_boot_time TIMESTAMPTZ,
  last_heartbeat TIMESTAMPTZ,
  agent_version VARCHAR(20),
  screenconnect_guid VARCHAR(255),
  is_online BOOLEAN DEFAULT false,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE asset_network_adapters (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  adapter_name VARCHAR(255),
  adapter_type VARCHAR(10) CHECK (adapter_type IN ('ethernet', 'wifi', 'other')),
  mac_address VARCHAR(20),
  ip_address VARCHAR(50),
  subnet_mask VARCHAR(50),
  default_gateway VARCHAR(50),
  dns_servers TEXT[],
  dhcp_enabled BOOLEAN,
  is_primary BOOLEAN DEFAULT false,
  wifi_ssid VARCHAR(100),
  wifi_signal_strength INTEGER,
  wifi_link_speed VARCHAR(50),
  wifi_frequency_band VARCHAR(10),
  reported_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE asset_disks (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  drive_letter VARCHAR(5),
  label VARCHAR(100),
  total_gb DECIMAL(10,2),
  used_gb DECIMAL(10,2),
  free_gb DECIMAL(10,2),
  file_system VARCHAR(20),
  reported_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE asset_smart_data (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  disk_identifier VARCHAR(255),
  smart_status VARCHAR(10) CHECK (smart_status IN ('healthy', 'warning', 'critical', 'unknown')),
  attributes JSONB,
  reported_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE asset_installed_apps (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  app_name VARCHAR(500) NOT NULL,
  app_version VARCHAR(100),
  publisher VARCHAR(255),
  install_date DATE,
  reported_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_installed_apps_name ON asset_installed_apps(app_name);
CREATE INDEX idx_installed_apps_asset ON asset_installed_apps(asset_id);

CREATE TABLE asset_heartbeats (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  reported_at TIMESTAMPTZ DEFAULT NOW(),
  cpu_usage DECIMAL(5,2),
  ram_usage_percent DECIMAL(5,2),
  uptime_seconds BIGINT,
  agent_version VARCHAR(20)
);

CREATE INDEX idx_heartbeats_asset_time ON asset_heartbeats(asset_id, reported_at DESC);
```

### Windows Update Management

```sql
CREATE TABLE update_policies (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  schedule_start_time TIME NOT NULL,
  schedule_frequency VARCHAR(10) NOT NULL CHECK (schedule_frequency IN ('daily', 'weekly', 'biweekly', 'monthly')),
  schedule_weekday VARCHAR(3) CHECK (schedule_weekday IN ('mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun')),
  schedule_weekday_interval VARCHAR(10) CHECK (schedule_weekday_interval IN ('first', 'second', 'third', 'fourth', 'last')),
  schedule_day_of_month INTEGER,
  schedule_interval VARCHAR(15) DEFAULT 'every',
  run_if_offline_at_next_boot BOOLEAN DEFAULT false,
  reboot_behavior VARCHAR(25) NOT NULL CHECK (reboot_behavior IN ('auto_reboot', 'prompt_with_deadline', 'no_reboot')),
  reboot_message TEXT,
  reboot_deadline_time TIME,
  severity_critical VARCHAR(10) DEFAULT 'approve',
  severity_important VARCHAR(10) DEFAULT 'approve',
  severity_moderate VARCHAR(10) DEFAULT 'manual',
  severity_low VARCHAR(10) DEFAULT 'manual',
  severity_other VARCHAR(10) DEFAULT 'manual',
  category_critical_updates VARCHAR(10) DEFAULT 'approve',
  category_update_rollups VARCHAR(10) DEFAULT 'approve',
  category_service_packs VARCHAR(10) DEFAULT 'manual',
  category_feature_packs VARCHAR(10) DEFAULT 'manual',
  category_definition_packs VARCHAR(10) DEFAULT 'approve',
  category_drivers VARCHAR(10) DEFAULT 'manual',
  category_other VARCHAR(10) DEFAULT 'manual',
  deferred_security_days INTEGER DEFAULT 1,
  deferred_patch_days INTEGER DEFAULT 1,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE update_policy_exclusions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  policy_id UUID NOT NULL REFERENCES update_policies(id) ON DELETE CASCADE,
  kb_number VARCHAR(20) NOT NULL,
  description TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE global_patch_exclusions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  kb_number VARCHAR(20) NOT NULL,
  description TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE asset_update_policy (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID UNIQUE NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  policy_id UUID NOT NULL REFERENCES update_policies(id),
  assigned_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE asset_installed_patches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  kb_number VARCHAR(20) NOT NULL,
  title VARCHAR(500),
  installed_on TIMESTAMPTZ,
  reported_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_installed_patches_kb ON asset_installed_patches(kb_number);
CREATE INDEX idx_installed_patches_asset ON asset_installed_patches(asset_id);

CREATE TABLE asset_pending_patches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  kb_number VARCHAR(20) NOT NULL,
  title VARCHAR(500),
  severity VARCHAR(20),
  category VARCHAR(50),
  reported_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_pending_patches_kb ON asset_pending_patches(kb_number);

CREATE TABLE update_job_runs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id),
  policy_id UUID REFERENCES update_policies(id),
  started_at TIMESTAMPTZ,
  completed_at TIMESTAMPTZ,
  status VARCHAR(15) CHECK (status IN ('pending', 'downloading', 'installing', 'rebooting', 'success', 'failed')),
  kbs_installed TEXT[],
  kbs_failed TEXT[],
  error_message TEXT,
  reboot_required BOOLEAN DEFAULT false,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE adhoc_patch_jobs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  kb_number VARCHAR(20) NOT NULL,
  kb_title VARCHAR(500),
  created_by UUID NOT NULL REFERENCES users(id),
  concurrency_limit INTEGER DEFAULT 20,
  scope_filter JSONB NOT NULL,
  reboot_behavior VARCHAR(25) NOT NULL CHECK (reboot_behavior IN ('auto_reboot', 'prompt_with_deadline', 'no_reboot')),
  status VARCHAR(15) CHECK (status IN ('active', 'paused', 'completed', 'cancelled')),
  total_assets INTEGER DEFAULT 0,
  completed_count INTEGER DEFAULT 0,
  failed_count INTEGER DEFAULT 0,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE adhoc_patch_job_assets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  job_id UUID NOT NULL REFERENCES adhoc_patch_jobs(id) ON DELETE CASCADE,
  asset_id UUID NOT NULL REFERENCES assets(id),
  status VARCHAR(20) CHECK (status IN ('queued', 'downloading', 'installing', 'success', 'failed', 'pending_reboot')),
  error_message TEXT,
  started_at TIMESTAMPTZ,
  completed_at TIMESTAMPTZ,
  queue_position INTEGER
);
```

### Agent Management

```sql
CREATE TABLE agent_versions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  version VARCHAR(20) NOT NULL,
  filename VARCHAR(255) NOT NULL,
  file_path VARCHAR(500) NOT NULL,
  file_hash VARCHAR(100) NOT NULL,
  release_notes TEXT,
  is_current BOOLEAN DEFAULT false,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE agent_rollout_groups (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(100) NOT NULL,
  target_version_id UUID NOT NULL REFERENCES agent_versions(id),
  scope_type VARCHAR(10) CHECK (scope_type IN ('customer', 'asset')),
  scope_ids UUID[] NOT NULL,
  status VARCHAR(15) CHECK (status IN ('active', 'completed', 'paused')),
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE agent_update_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id),
  from_version VARCHAR(20),
  to_version VARCHAR(20),
  status VARCHAR(15) CHECK (status IN ('success', 'failed', 'rolled_back')),
  error_message TEXT,
  updated_at TIMESTAMPTZ DEFAULT NOW()
);
```

### Alerts and Notifications

```sql
CREATE TABLE alert_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(100) NOT NULL,
  alert_type VARCHAR(25) NOT NULL CHECK (alert_type IN ('server_offline', 'disk_space_low', 'smart_warning', 'agent_update_failed')),
  threshold_value VARCHAR(50),
  applies_to VARCHAR(20) CHECK (applies_to IN ('server_only', 'workstation_only', 'all')),
  creates_ticket BOOLEAN DEFAULT true,
  is_active BOOLEAN DEFAULT true,
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE alerts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  alert_rule_id UUID NOT NULL REFERENCES alert_rules(id),
  asset_id UUID NOT NULL REFERENCES assets(id),
  ticket_id UUID REFERENCES tickets(id),
  message TEXT NOT NULL,
  severity VARCHAR(10) CHECK (severity IN ('warning', 'critical')),
  is_acknowledged BOOLEAN DEFAULT false,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  acknowledged_at TIMESTAMPTZ
);

CREATE TABLE notifications (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES users(id),
  event_type VARCHAR(25) NOT NULL CHECK (event_type IN ('ticket_assigned', 'customer_reply', 'ticket_reassigned', 'alert_created')),
  reference_type VARCHAR(10) CHECK (reference_type IN ('ticket', 'asset', 'alert')),
  reference_id UUID,
  title VARCHAR(255) NOT NULL,
  message TEXT,
  is_read BOOLEAN DEFAULT false,
  email_sent BOOLEAN DEFAULT false,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  read_at TIMESTAMPTZ
);

CREATE INDEX idx_notifications_user ON notifications(user_id, is_read);
```

### Remote Terminal

```sql
CREATE TABLE terminal_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id),
  user_id UUID NOT NULL REFERENCES users(id),
  shell_type VARCHAR(15) CHECK (shell_type IN ('cmd', 'powershell')),
  started_at TIMESTAMPTZ DEFAULT NOW(),
  ended_at TIMESTAMPTZ,
  status VARCHAR(15) CHECK (status IN ('active', 'closed', 'disconnected'))
);

CREATE TABLE terminal_command_log (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES terminal_sessions(id) ON DELETE CASCADE,
  user_id UUID NOT NULL REFERENCES users(id),
  command TEXT NOT NULL,
  output TEXT,
  executed_at TIMESTAMPTZ DEFAULT NOW()
);
```

### System Settings

```sql
CREATE TABLE system_settings (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  key VARCHAR(100) UNIQUE NOT NULL,
  value TEXT NOT NULL,
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Default settings to seed:
-- screenconnect_url: 'https://cbit.screenconnect.com'
-- screenconnect_instance_id: '<instance_id>'
-- offline_threshold_minutes: '15'
-- disk_space_alert_gb: '10'
-- support_email: 'support@gocbit.com'
-- graph_api_tenant_id: '<tenant_id>'
-- graph_api_client_id: '<client_id>'
-- graph_api_client_secret: '<encrypted_secret>'
-- agent_check_in_interval_minutes: '5'
-- stale_ticket_days: '7'
-- aging_ticket_days: '10'
```

---

## Agent-to-Server API Contract

### Agent Registration (POST /api/agent/register)

Called once during first-time agent startup after installation.

**Request:**
```json
{
  "customer_key": "unique-key-baked-into-msi",
  "hostname": "WORKSTATION-01",
  "os_type": "windows_workstation",
  "os_version": "Windows 11 Pro",
  "agent_version": "1.0.0"
}
```

**Response:**
```json
{
  "agent_id": "generated-uuid",
  "agent_token": "jwt-token-for-future-auth",
  "check_in_interval_minutes": 5
}
```

The agent stores agent_id and agent_token locally and uses them for all future communication.

### Agent Check-In (POST /api/agent/checkin)

Sent on a regular interval (default every 5 minutes). This is the primary data sync mechanism.

**Headers:**
```
Authorization: Bearer <agent_token>
X-Agent-ID: <agent_id>
```

**Request:**
```json
{
  "agent_id": "unique-agent-identifier",
  "agent_version": "1.0.0",
  "timestamp": "2026-02-22T10:00:00Z",
  "system_info": {
    "hostname": "WORKSTATION-01",
    "os_type": "windows_workstation",
    "os_version": "Windows 11 Pro",
    "os_build": "26100.7623",
    "manufacturer": "Dell Inc.",
    "model": "OptiPlex Micro 7020",
    "serial_number": "ABC1234",
    "cpu_model": "Intel Core i7-14700",
    "cpu_cores": 20,
    "ram_gb": 16.0,
    "domain": "CONTOSO",
    "last_user": "CONTOSO\\jsmith",
    "last_boot_time": "2026-02-20T17:01:00Z",
    "uptime_seconds": 147600
  },
  "network_adapters": [
    {
      "name": "Intel Ethernet I219-LM",
      "type": "ethernet",
      "mac_address": "AA:BB:CC:DD:EE:FF",
      "ip_address": "10.10.0.78",
      "subnet_mask": "255.255.255.0",
      "default_gateway": "10.10.0.1",
      "dns_servers": ["10.10.0.5", "8.8.8.8"],
      "dhcp_enabled": true,
      "is_primary": true,
      "wifi_ssid": null,
      "wifi_signal_strength": null,
      "wifi_link_speed": null,
      "wifi_frequency_band": null
    },
    {
      "name": "Intel Wi-Fi 6E AX211",
      "type": "wifi",
      "mac_address": "11:22:33:44:55:66",
      "ip_address": "192.168.1.105",
      "subnet_mask": "255.255.255.0",
      "default_gateway": "192.168.1.1",
      "dns_servers": ["192.168.1.1"],
      "dhcp_enabled": true,
      "is_primary": false,
      "wifi_ssid": "CONTOSO-WIFI",
      "wifi_signal_strength": 72,
      "wifi_link_speed": "1200 Mbps",
      "wifi_frequency_band": "5GHz"
    }
  ],
  "disks": [
    {
      "drive_letter": "C:",
      "label": "OS",
      "total_gb": 476.0,
      "used_gb": 234.5,
      "free_gb": 241.5,
      "file_system": "NTFS"
    }
  ],
  "smart_data": [
    {
      "disk_identifier": "SAMSUNG MZVL21T0HCLR",
      "status": "healthy",
      "attributes": {}
    }
  ],
  "screenconnect_guid": "abc123-session-guid",
  "wan_ip": "66.119.214.135"
}
```

**Response:**
```json
{
  "status": "ok",
  "commands": [
    {
      "type": "update_agent",
      "version": "1.1.0",
      "download_url": "/api/agent/download/1.1.0",
      "file_hash": "sha256:abc123..."
    },
    {
      "type": "install_kb",
      "kb_number": "KB5077181",
      "job_id": "uuid-of-adhoc-job"
    },
    {
      "type": "run_updates",
      "policy_id": "uuid-of-policy",
      "policy": {
        "severity_critical": "approve",
        "severity_important": "approve",
        "category_drivers": "manual",
        "reboot_behavior": "prompt_with_deadline",
        "reboot_message": "Scheduled Windows Updates from CBIT.",
        "reboot_deadline_time": "01:00",
        "excluded_kbs": ["KB5065789", "KB5066385"]
      }
    }
  ],
  "check_in_interval_minutes": 5
}
```

### Installed Apps Report (POST /api/agent/apps)

Sent less frequently (once per hour or on change detection).

**Request:**
```json
{
  "agent_id": "unique-agent-identifier",
  "apps": [
    {
      "name": "Google Chrome",
      "version": "122.0.6261.112",
      "publisher": "Google LLC",
      "install_date": "2026-01-15"
    }
  ]
}
```

### Patch Status Report (POST /api/agent/patches)

Sent after patch scans and after update runs.

**Request:**
```json
{
  "agent_id": "unique-agent-identifier",
  "installed_patches": [
    {
      "kb_number": "KB5077181",
      "title": "2026-02 Security Update",
      "installed_on": "2026-02-14T22:30:00Z"
    }
  ],
  "pending_patches": [
    {
      "kb_number": "KB5075912",
      "title": "2026-02 Cumulative Update for Windows 10",
      "severity": "critical",
      "category": "critical_updates"
    }
  ]
}
```

### Update Job Result (POST /api/agent/update-result)

Sent after a scheduled or ad-hoc update run completes.

**Request:**
```json
{
  "agent_id": "unique-agent-identifier",
  "job_type": "scheduled",
  "policy_id": "uuid-or-null",
  "adhoc_job_id": "uuid-or-null",
  "started_at": "2026-02-19T22:00:00Z",
  "completed_at": "2026-02-19T22:45:00Z",
  "status": "success",
  "kbs_installed": ["KB5077181", "KB5075912"],
  "kbs_failed": [],
  "error_message": null,
  "reboot_required": true
}
```

### Agent Update Result (POST /api/agent/update-agent-result)

Sent after an agent self-update attempt.

**Request:**
```json
{
  "agent_id": "unique-agent-identifier",
  "from_version": "1.0.0",
  "to_version": "1.1.0",
  "status": "success",
  "error_message": null
}
```

### Support Request from Tray (POST /api/agent/support-request)

Sent when end user submits a support request from the system tray icon.

**Request (multipart/form-data):**
```
agent_id: "unique-agent-identifier"
description: "My printer isn't working"
screenshot: <binary image file>
contact_name: "John Smith" (from logged-in Windows user)
```

Server creates a ticket auto-assigned to the correct customer, attaches screenshot, links the asset.

### WebSocket Connection (WSS /api/agent/ws)

Used for real-time remote terminal sessions.

**Auth:** Agent token sent as query parameter on connection.

**Server to Agent messages:**
```json
{ "type": "terminal_start", "session_id": "uuid", "shell_type": "powershell" }
{ "type": "terminal_input", "session_id": "uuid", "data": "Get-Process\r\n" }
{ "type": "terminal_stop", "session_id": "uuid" }
```

**Agent to Server messages:**
```json
{ "type": "terminal_output", "session_id": "uuid", "data": "Handles  NPM(K)..." }
{ "type": "terminal_started", "session_id": "uuid" }
{ "type": "terminal_error", "session_id": "uuid", "error": "Failed to start PowerShell" }
```

---

## Email Integration (Microsoft Graph API)

### Inbound Email Flow

1. Register a Graph API application in Azure AD with Mail.Read and Mail.Send permissions for support@gocbit.com
2. Use Graph API webhooks or polling to detect new emails
3. For each new email:
   a. Check if reply to existing ticket by matching: email conversation ID, subject line ticket number pattern [Ticket #XXXXX], In-Reply-To headers
   b. If match: append communication to existing ticket, set status to "Customer Reply"
   c. If no match: create new ticket, match sender email to contacts (exact match first), then domain match to customer, else create unassigned
4. Store Graph API message ID for threading
5. Download and store attachments

### Outbound Email Flow

1. Tech selects "Email" mode in ticket communications
2. System sends via Graph API using support@gocbit.com mailbox
3. Subject includes [Ticket #XXXXX] for threading
4. Sets In-Reply-To header for email thread continuity
5. Includes CC recipients from ticket_cc
6. Email appears in Outlook Sent Items
7. Communication stored with comm_type = 'email' and Graph message ID

---

## Feature Specifications

### 1. Tech Dashboard

Default landing page. Metric cards: New Today, Customer Replies, Total Open, Aging (10+ days), Avg First Response (MTD), Avg Resolution Time (MTD). Ticket list grouped by priority (Urgent, High, Normal, Low, Not Set). Columns: ticket number, customer, subject, status, last updated with aging badges, created, type, priority. Default filter shows assigned tickets, dropdown to switch to All or Unassigned. Search bar.

### 2. Coordinator/Admin Dashboard

Same layout, defaults to all tickets. Not Set section highlights unassigned queue. Team-wide metrics.

### 3. Customer Page

Header with name and status. Overview sidebar with ticket counts, avg time per ticket. Information section with Microsoft tenant, email, phone, address. Custom fields section (all admin-defined fields). Network section with notes, summary, circuit ID, IP, subnet. Contacts tab with end user list. Assets tab with abbreviated asset list. Tickets tab with abbreviated ticket list. Download Agent MSI button.

### 4. Ticket Page

Left sidebar with ticket info (status, priority, assignee, type, created date, CCs) and customer info (name, contact, email, phones, address). Main area with worksheets section (attach templates, progress tracking, finalize, required items block ticket closure). Relevant assets section. Communications thread (chronological, author name and type badge, rich text body, inline attachments, email/private note toggle, rich text editor, attachment upload). Linked tickets section (child/parent).

### 5. Asset Page

Top bar with asset name, online/offline badge, Remote Access button (ScreenConnect), Terminal button. Owner info section. System Info tab with all agent-reported hardware and OS info, network adapters table with WiFi details, disk table, SMART status. Installed Apps tab with searchable table. Windows Patches tab with installed and pending patches, assigned policy, last update result. Terminal tab with web terminal. Update History tab.

### 6. Remote Terminal

Tech clicks Terminal on asset page. Frontend opens WebSocket to backend. Backend relays to agent WebSocket. Agent spawns shell process, pipes I/O. Frontend renders via xterm.js. Supports CMD and PowerShell. Interactive sessions with tab completion. Ctrl+C support. Session timeout. Multiple concurrent sessions. All commands logged for audit.

### 7. Windows Update Policies

Admin CRUD for policies. Full granularity: severity approval matrix (Critical/Important/Moderate/Low/Other x Approve/Defer/Reject/Manual), category approval matrix (Critical Updates/Update Rollups/Service Packs/Feature Packs/Definition Packs/Drivers/Other x Approve/Defer/Reject/Manual), deferred time periods, schedule (time/frequency/weekday/interval), if-offline-run-at-boot, per-policy KB exclusions, global KB exclusions, reboot behavior/message/deadline. Policy assignment to assets.

### 8. Ad-Hoc KB Push

From Missing Patches report, click Install. Configure scope (all missing, filter by customer/server/workstation), concurrency limit, reboot behavior, optional test group. Creates job with queue. Live progress view. Pause/cancel. Per-asset status visible on asset pages.

### 9. Alert System

Rules: Server Offline (server only, threshold minutes), Disk Space Low (all, threshold GB), SMART Warning (all), Agent Update Failed (all). Background job checks every minute. De-duplicates open alerts. Auto-creates tickets where configured. Windows Update failures tracked in DB only, surfaced in reports.

### 10. Notification System

Events: ticket_assigned, customer_reply, ticket_reassigned, alert_created. Methods per tech per event: in-app, email, both, none. In-app bell icon with unread count. Email opt-in layer.

### 11. Agent Tray Support Request

System tray icon near clock. Right-click to submit request. Form with description and screenshot capture. POSTs to server. Auto-creates ticket linked to customer and asset.

### 12. Agent Auto-Update

Admin uploads new version. Creates rollout group (name, version, scope: specific customers or assets). Activates group. Monitors progress. Promotes to current (global) when verified. Agent checks on heartbeat, downloads, verifies hash, stops/replaces/restarts service. Reports result. Watchdog script reverts on failure.

### 13. ScreenConnect Integration

Admin stores URL and Instance ID. Agent detects ScreenConnect client service, reads session GUID, reports in heartbeat. UI button opens https://cbit.screenconnect.com/Host#Access/Session/{guid} in new tab.

### 14. Global Software Search

Dedicated page. Search by app name (partial match). Filter by server/workstation, customer. Results table: asset name, customer, version, OS type, online status. Count of matches. Sortable. Click-through to asset.

### 15. Executive Metrics Dashboard

Summary cards: Total Created, Open, Resolved, Avg Resolve Time, Avg First Response, Urgent, This Week, Unique Customers. Charts: daily volume, by day of week, by hour, priority distribution (doughnut), resolution time distribution, resolution time by priority. Backlog: aging buckets, status distribution, stale tickets list. Customer health table (top 15 by volume with trends). Customer concentration Pareto chart. Tickets by type. Date range picker, auto-refresh, customer exclusions, tech exclusions for first response.

### 16. Technician Efficiency Dashboard

Team summary cards and performance metrics. Per-tech cards with efficiency scoring (daily 50/40/10, weekly 60/30/10). Metrics: total, resolved, open, workable, non-workable, aging, FCR, avg resolution, avg active resolution, communication counts, stale warnings, resolved within 48hrs. Leaderboard. Dual-period comparison. Score classification (Excellent/Good/Average/Poor).

### 17. Missing Patches Report

Filters: customer name, ignore stale assets, stale age, include globally-blocked KBs. Results: KB number, title, count of assets missing, sorted by count. Install button per row. PDF export.

---

## Admin Panel

Users management (CRUD, roles, notification preferences). Ticket configuration (statuses, priorities, types — all CRUD with display order). Custom field definitions (CRUD, types, dropdown options, display order, required flag). Worksheet templates (CRUD, items with type/required/order). Update policies (full CRUD). Global patch exclusions (CRUD). Alert rules (thresholds, active toggle, auto-ticket toggle). Agent versions (upload, rollout groups, progress monitoring). System settings (ScreenConnect, thresholds, email config, Graph API credentials, intervals). Notification configuration (per-user per-event method).

---

## Build Phases

| Phase | Name | Dependencies | Key Deliverables |
|-------|------|-------------|-----------------|
| 1 | Foundation | None | Project scaffolding, database, auth, basic UI shell |
| 2 | Customer and Contact Management | Phase 1 | Customer CRUD, contacts, custom fields |
| 3 | Ticketing Core | Phase 2 | Tickets, communications, worksheets, linked tickets |
| 4 | Email Integration | Phase 3 | Graph API inbound/outbound, auto-create, threading |
| 5 | RMM Agent v1 | Phase 1 | C# agent, registration, check-in, system info, auto-update |
| 6 | Remote Terminal | Phase 5 | WebSocket terminal, CMD/PowerShell, audit logging |
| 7 | Windows Update Management | Phase 5 | Policies, scheduling, agent-side updates, ad-hoc push |
| 8 | Alerts and Notifications | Phases 3, 5 | Alert rules, auto-tickets, in-app and email notifications |
| 9 | Dashboards and Reporting | Phases 3, 5, 7 | Executive metrics, tech efficiency, missing patches |
| 10 | Agent Tray App | Phase 5 | System tray icon, support request with screenshot |

---

## v1.1 Backlog (Not in v1.0)

- Recent Activity feed on assets (app install/uninstall/update tracking)
- Tags on tickets and customers
- SLA tracking
- Appointments/scheduling
- Task Manager, Service Manager, Event Viewer, File System, Registry Editor (remote agent tools)
- Additional report types
- Mobile-responsive UI improvements
- API rate limiting and advanced security hardening
- Bulk operations on tickets
- Saved/favorite report configurations
- Customer portal
- Mac agent support
