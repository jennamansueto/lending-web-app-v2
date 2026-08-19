import { Routes } from '@angular/router';
import { loadRemoteModule } from '@angular-architects/native-federation';
import { Home } from './home';

export const routes: Routes = [
  { path: '', component: Home, pathMatch: 'full' },
  {
    path: 'loan-application',
    loadComponent: () => loadRemoteModule('loan-application', './Component').then((m) => m.App),
  },
  {
    path: 'pricing',
    loadComponent: () => loadRemoteModule('pricing', './Component').then((m) => m.App),
  },
  {
    path: 'borrower-lookup',
    loadComponent: () => loadRemoteModule('borrower-lookup', './Component').then((m) => m.App),
  },
  {
    path: 'statements',
    loadComponent: () => loadRemoteModule('statements', './Component').then((m) => m.App),
  },
];
