#include <il2cxx/object.h>

namespace il2cxx
{

IL2CXX__PORTABLE__THREAD decltype(t_object::v_roots) t_object::v_roots;
IL2CXX__PORTABLE__THREAD t_object* t_object::v_scan_stack;
IL2CXX__PORTABLE__THREAD t_object* t_object::v_cycle;
IL2CXX__PORTABLE__THREAD t_object* t_object::v_cycles;

void t_object::f_collect()
{
	while (v_cycles) {
		std::lock_guard<std::mutex> lock(f_engine()->v_object__reviving__mutex);
		auto cycle = v_cycles;
		v_cycles = cycle->v_next_cycle;
		auto p = cycle;
		auto mutated = [&]
		{
			if (f_engine()->v_object__reviving)
				do {
					if (p->v_color != e_color__ORANGE || p->v_cyclic > 0) return true;
					if (auto q = p->v_extension.load(std::memory_order_relaxed)) if (q->v_weak_handles__cycle) return true;
				} while ((p = p->v_next) != cycle);
			else
				do if (p->v_color != e_color__ORANGE || p->v_cyclic > 0) return true; while ((p = p->v_next) != cycle);
			return false;
		};
		if (mutated()) {
			p = cycle;
			auto q = p->v_next;
			if (p->v_color == e_color__ORANGE) {
				p->v_color = e_color__PURPLE;
				f_append(p);
			} else if (p->v_color == e_color__PURPLE) {
				f_append(p);
			} else {
				p->v_color = e_color__BLACK;
				p->v_next = nullptr;
			}
			while (q != cycle) {
				p = q;
				q = p->v_next;
				if (p->v_color == e_color__PURPLE) {
					f_append(p);
				} else {
					p->v_color = e_color__BLACK;
					p->v_next = nullptr;
				}
			}
		} else {
			auto finalizee = false;
			do if (p->v_finalizee) finalizee = true; while ((p = p->v_next) != cycle);
			if (finalizee) {
				auto& conductor = f_engine()->v_finalizer__conductor;
				std::lock_guard<std::mutex> lock(conductor.v_mutex);
				if (conductor.v_quitting) {
					finalizee = false;
				} else {
					auto& queue = f_engine()->v_finalizer__queue;
					do {
						if (auto q = p->v_extension.load(std::memory_order_relaxed)) q->f_detach();
						auto q = p->v_next;
						p->v_color = e_color__BLACK;
						p->v_next = nullptr;
						if (p->v_finalizee) {
							++p->v_count;
							queue.push_back(p);
						}
						p = q;
					} while (p != cycle);
					conductor.f__wake();
				}
			}
			if (!finalizee) {
				do p->v_color = e_color__RED; while ((p = p->v_next) != cycle);
				do p->f_cyclic_decrement(); while ((p = p->v_next) != cycle);
				do {
					auto q = p->v_next;
					f_engine()->f_free_as_collect(p);
					p = q;
				} while (p != cycle);
			}
		}
	}
	auto roots = reinterpret_cast<t_object*>(&v_roots);
	if (roots->v_next == roots) return;
	{
		size_t live = f_engine()->v_object__pool0.f_live() + f_engine()->v_object__pool1.f_live() + f_engine()->v_object__pool2.f_live() + f_engine()->v_object__pool3.f_live() + f_engine()->v_object__allocated - f_engine()->v_object__freed;
		auto& lower = f_engine()->v_object__lower;
		if (live < lower) lower = live;
		if (live - lower < f_engine()->v_collector__threshold) return;
		lower = live;
		++f_engine()->v_collector__collect;
		auto p = roots;
		auto q = p->v_next;
		do {
			assert(q->v_count > 0);
			if (q->v_color == e_color__PURPLE) {
				q->f_mark_gray();
				p = q;
			} else {
				p->v_next = q->v_next;
				q->v_next = nullptr;
			}
			q = p->v_next;
		} while (q != roots);
	}
	if (roots->v_next == roots) {
		roots->v_previous = roots;
		return;
	}
	{
		auto p = roots->v_next;
		do {
			p->f_scan_gray();
			p = p->v_next;
		} while (p != roots);
	}
	do {
		auto p = roots->v_next;
		roots->v_next = p->v_next;
		if (p->v_color == e_color__WHITE) {
			p->f_collect_white();
			v_cycle->v_next_cycle = v_cycles;
			v_cycles = v_cycle;
		} else {
			p->v_next = nullptr;
		}
	} while (roots->v_next != roots);
	roots->v_previous = roots;
	for (auto cycle = v_cycles; cycle; cycle = cycle->v_next_cycle) {
		auto p = cycle;
		do {
			p->v_color = e_color__RED;
			p->v_cyclic = p->v_count;
		} while ((p = p->v_next) != cycle);
		do p->f_step<&t_object::f_scan_red>(); while ((p = p->v_next) != cycle);
		do p->v_color = e_color__ORANGE; while ((p = p->v_next) != cycle);
	}
}

void t_object::f_cyclic_decrement()
{
	if (auto p = v_extension.load(std::memory_order_consume)) {
		p->f_scan(f_push_and_clear<&t_object::f_cyclic_decrement_push>);
		v_extension.store(nullptr, std::memory_order_relaxed);
		delete p;
	}
	v_type->f_scan(this, f_push_and_clear<&t_object::f_cyclic_decrement_push>);
	v_type->f_cyclic_decrement_push();
}

t__extension* t_object::f_extension()
{
	auto p = v_extension.load(std::memory_order_consume);
	if (p) return p;
	t__extension* q = nullptr;
	p = new t__extension();
	if (v_extension.compare_exchange_strong(q, p, std::memory_order_consume)) return p;
	delete p;
	return q;
}

t__extension::~t__extension()
{
	for (auto p = v_weak_handles.v_next; p != static_cast<t__weak_handle*>(&v_weak_handles); p = p->v_next) p->v_target = nullptr;
}

void t__extension::f_detach()
{
	for (auto p = v_weak_handles.v_next; p != static_cast<t__weak_handle*>(&v_weak_handles); p = p->v_next) {
		if (p->v_final) continue;
		p->v_target = nullptr;
		p->v_previous->v_next = p->v_next;
		p->v_next->v_previous = p->v_previous;
	}
}

void t__extension::f_scan(t_scan a_scan)
{
	std::lock_guard<std::mutex> lock(v_weak_handles__mutex);
	a_scan(v_weak_handles__cycle);
	for (auto p = v_weak_handles.v_next; p != static_cast<t__weak_handle*>(&v_weak_handles); p = p->v_next) p->f_scan(a_scan);
}

t_scoped<t_slot> t__normal_handle::f_target() const
{
	return v_target;
}

void t__weak_handle::f_attach()
{
	auto extension = v_target->f_extension();
	std::lock_guard<std::mutex> lock1(extension->v_weak_handles__mutex);
	if (!extension->v_weak_handles__cycle) extension->v_weak_handles__cycle = v_target;
	v_previous = extension->v_weak_handles.v_previous;
	v_next = static_cast<t__weak_handle*>(&extension->v_weak_handles);
	v_previous->v_next = v_next->v_previous = this;
}

void t__weak_handle::f_detach()
{
	if (!v_target) return;
	auto extension = v_target->v_extension.load(std::memory_order_relaxed);
	std::lock_guard<std::mutex> lock1(extension->v_weak_handles__mutex);
	v_previous->v_next = v_next;
	v_next->v_previous = v_previous;
	if (extension->v_weak_handles.v_next == static_cast<t__weak_handle*>(&extension->v_weak_handles)) extension->v_weak_handles__cycle = nullptr;
}

t__weak_handle::t__weak_handle(t_object* a_target, bool a_final) : v_target(a_target), v_final(a_final)
{
	std::lock_guard<std::mutex> lock0(f_engine()->v_object__reviving__mutex);
	f_attach();
}

t__weak_handle::~t__weak_handle()
{
	std::lock_guard<std::mutex> lock0(f_engine()->v_object__reviving__mutex);
	f_detach();
}

t_scoped<t_slot> t__weak_handle::f_target() const
{
	std::lock_guard<std::mutex> lock(f_engine()->v_object__reviving__mutex);
	f_engine()->v_object__reviving = true;
	t_thread::v_current->f_revive();
	return v_target;
}

void t__weak_handle::f_target__(t_object* a_p)
{
	std::lock_guard<std::mutex> lock(f_engine()->v_object__reviving__mutex);
	f_detach();
	v_target = a_p;
	f_attach();
}

void t__weak_handle::f_scan(t_scan a_scan)
{
}

void t__dependent_handle::f_scan(t_scan a_scan)
{
	a_scan(v_secondary);
}

}
