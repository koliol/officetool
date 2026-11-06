import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import * as ElementPlusIconsVue from '@element-plus/icons-vue'

import App from './App.vue'
import './styles.css'
import { startRouter } from './router'
import { installAuthGuards } from './auth'
import PageHeader from './components/PageHeader.vue'

const app = createApp(App)

for (const [key, component] of Object.entries(ElementPlusIconsVue)) {
  app.component(key, component)
}

// 页头在每个一级页面都要用，注册成全局组件省掉 5 处重复 import
app.component('PageHeader', PageHeader)

app.use(createPinia())

// 401 的全局跳转。必须在 pinia 装好之后注册：回调里要取 store，
// 而 store 的实例化依赖已激活的 pinia。
installAuthGuards()

app.use(ElementPlus, { locale: zhCn })

// 先确定当前路由再挂载，避免首帧渲染出错误页面再跳走
startRouter()

app.mount('#app')
