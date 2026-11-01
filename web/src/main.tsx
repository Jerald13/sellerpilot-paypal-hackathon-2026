import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import ShopApp from './ShopApp.tsx'

// Two apps, one bundle: /shop is the customer storefront, everything else is the seller's back office.
const isShop = window.location.pathname.startsWith('/shop')

createRoot(document.getElementById('root')!).render(<StrictMode>{isShop ? <ShopApp /> : <App />}</StrictMode>)
