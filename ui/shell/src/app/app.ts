import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';

// BR-UI-013: the status bar is hard-coded in the legacy MainForm — it claims PROD and the
// ORCL service regardless of the actual connection. The legacy text appends the uppercased
// OS user name; a browser has none, so a fixed workstation identity is rendered instead.
const STATUS_USER = 'LENDINGDESK';

@Component({
  imports: [RouterOutlet, RouterLink],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly statusBarText =
    'Connected: PROD (ORCL/lending)   |   User: ' + STATUS_USER;
}
