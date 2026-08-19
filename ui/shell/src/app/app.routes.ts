import { Routes } from '@angular/router';
import { loadRemoteModule } from '@angular-architects/native-federation';
import { Home } from './home';

export const routes: Routes = [
  { path: '', component: Home, pathMatch: 'full' },
  {
    path: 'loan-application',
    loadComponent: () => loadRemoteModule('loanApplication', './Component').then((m) => m.App),
  },
  {
    path: 'pricing',
    loadComponent: () => loadRemoteModule('pricing', './Component').then((m) => m.App),
  },
  {
    path: 'borrower-lookup',
    loadComponent: () => loadRemoteModule('borrowerLookup', './Component').then((m) => m.App),
  },
  {
    path: 'statements',
    loadComponent: () => loadRemoteModule('statements', './Component').then((m) => m.App),
  },
];
