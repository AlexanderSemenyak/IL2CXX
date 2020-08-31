#include <il2cxx/engine.h>

namespace il2cxx
{

void t_engine::f_collector()
{
	t_slot::v_collector = this;
	if (v_options.v_verbose) std::fprintf(stderr, "collector starting...\n");
	t_object::v_roots.v_next = t_object::v_roots.v_previous = reinterpret_cast<t_object*>(&t_object::v_roots);
	while (true) {
		{
			std::unique_lock<std::mutex> lock(v_collector__conductor.v_mutex);
			v_collector__conductor.f__next(lock);
			if (v_collector__conductor.v_quitting) break;
		}
		++v_collector__epoch;
		{
			std::lock_guard<std::mutex> lock(v_object__reviving__mutex);
			v_object__reviving = false;
		}
		{
			std::lock_guard<std::mutex> lock(v_thread__mutex);
			auto p = &v_thread__internals;
			while (*p) {
				auto q = *p;
				auto active = q->v_done >= 0;
				if (active && q->v_next && !q->v_next->v_next) {
					active = v_finalizer__awaken;
					if (active && v_finalizer__sleeping) --v_finalizer__awaken;
				}
				if (active) {
					auto tail = q->v_increments.v_tail;
					q->f_epoch();
					std::lock_guard<std::mutex> lock(v_object__reviving__mutex);
					if (q->v_reviving) {
						size_t n = t_slot::t_increments::V_SIZE;
						size_t epoch = (q->v_increments.v_tail + n - tail) % n;
						size_t reviving = (q->v_reviving + n - tail) % n;
						if (epoch < reviving)
							v_object__reviving = true;
						else
							q->v_reviving = nullptr;
					}
				}
				if (q->v_done < 3) {
					p = &q->v_next;
				} else {
					*p = q->v_next;
					delete q;
				}
			}
		}
		t_object::f_collect();
		v_object__heap.f_flush();
	}
	if (v_options.v_verbose) std::fprintf(stderr, "collector quitting...\n");
	v_collector__conductor.f_exit();
}

void t_engine::f_finalizer(void(*a_finalize)(t_object*))
{
	if (v_options.v_verbose) std::fprintf(stderr, "finalizer starting...\n");
	while (true) {
		{
			std::lock_guard<std::mutex> lock(v_thread__mutex);
			v_finalizer__sleeping = true;
		}
		{
			std::unique_lock<std::mutex> lock(v_finalizer__conductor.v_mutex);
			if (v_finalizer__conductor.v_quitting) break;
			v_finalizer__conductor.f__next(lock);
		}
		{
			std::lock_guard<std::mutex> lock(v_thread__mutex);
			v_finalizer__sleeping = false;
			v_finalizer__awaken = 2;
		}
		[this, a_finalize]
		{
		char padding[4096];
		std::memset(padding, 0, sizeof(padding));
		[this, a_finalize]
		{
		while (true) {
			t_object* p;
			{
				std::unique_lock<std::mutex> lock(v_finalizer__conductor.v_mutex);
				if (v_finalizer__queue.empty()) break;
				p = v_finalizer__queue.front();
				v_finalizer__queue.pop_front();
			}
			p->f_type()->f_suppress_finalize(p);
			a_finalize(p);
			t_slot::t_decrements::f_push(p);
		}
		}();
		}();
	}
	if (v_options.v_verbose) std::fprintf(stderr, "finalizer quitting...\n");
	v_finalizer__conductor.f_exit();
}

t_engine::t_engine(const t_options& a_options, size_t a_count, char** a_arguments) : v_object__heap([]
{
	f_engine()->f_wait();
}), v_options(a_options), v_collector__threshold(v_options.v_collector__threshold)
{
	if (sem_init(&v_epoch__received, 0, 0) == -1) throw std::system_error(errno, std::generic_category());
	sigfillset(&v_epoch__notsigusr2);
	sigdelset(&v_epoch__notsigusr2, SIGUSR2);
	struct sigaction sa;
	sa.sa_handler = [](int)
	{
	};
	sigemptyset(&sa.sa_mask);
	sa.sa_flags = SA_RESTART;
	if (sigaction(SIGUSR2, &sa, &v_epoch__old_sigusr2) == -1) throw std::system_error(errno, std::generic_category());
	sa.sa_handler = [](int)
	{
		t_thread::v_current->f_epoch_get();
		f_engine()->f_epoch_suspend();
	};
	sigaddset(&sa.sa_mask, SIGUSR2);
	if (sigaction(SIGUSR1, &sa, &v_epoch__old_sigusr1) == -1) throw std::system_error(errno, std::generic_category());
}

t_engine::~t_engine()
{
	{
		auto internal = v_thread->v__internal;
		v_thread.f__destruct();
		internal->f_epoch_get();
		std::lock_guard<std::mutex> lock(v_thread__mutex);
		++internal->v_done;
	}
	f_wait();
	f_wait();
	f_wait();
	f_wait();
	v_collector__conductor.f_quit();
	assert(!v_thread__internals);
	if (sem_destroy(&v_epoch__received) == -1) std::exit(errno);
	if (sigaction(SIGUSR1, &v_epoch__old_sigusr1, NULL) == -1) std::exit(errno);
	if (sigaction(SIGUSR2, &v_epoch__old_sigusr2, NULL) == -1) std::exit(errno);
	if (v_options.v_verbose) {
		std::fprintf(stderr, "statistics:\n\tt_object:\n");
		size_t allocated = 0;
		size_t freed = 0;
		v_object__heap.f_statistics([&](auto a_rank, auto a_grown, auto a_allocated, auto a_freed)
		{
			std::fprintf(stderr, "\t\trank%zu: %zu: %zu - %zu = %zu\n", a_rank, a_grown, a_allocated, a_freed, a_allocated - a_freed);
			allocated += a_allocated;
			freed += a_freed;
		});
		std::fprintf(stderr, "\t\ttotal: %zu - %zu = %zu, release = %zu, collect = %zu\n", allocated, freed, allocated - freed, v_object__release, v_object__collect);
		std::fprintf(stderr, "\tcollector: tick = %zu, wait = %zu, epoch = %zu, collect = %zu\n", v_collector__tick, v_collector__wait, v_collector__epoch, v_collector__collect);
		if (allocated != freed) std::terminate();
	}
}

void t_engine::f_shutdown()
{
	{
		std::unique_lock<std::mutex> lock(v_thread__mutex);
		auto internal = v_thread->v__internal;
		while (true) {
			auto p = v_thread__internals;
			while (p && (p->v_done > 0 || p == internal || p->v_next == internal)) p = p->v_next;
			if (!p) break;
			v_thread__condition.wait(lock);
		}
	}
	v_shuttingdown = true;
	f_object__return();
	{
		std::unique_lock<std::mutex> lock(v_collector__conductor.v_mutex);
		if (v_collector__full++ <= 0) v_collector__threshold = 0;
	}
	f_wait();
	f_wait();
	f_wait();
	f_wait();
	assert(!v_thread__internals->v_next->v_next);
	{
		v_finalizer__conductor.f_quit();
		std::unique_lock<std::mutex> lock(v_thread__mutex);
		while (v_thread__internals->v_next && v_thread__internals->v_done <= 0) v_thread__condition.wait(lock);
	}
}

void t_engine::f_collect()
{
	{
		std::unique_lock<std::mutex> lock(v_collector__conductor.v_mutex);
		if (v_collector__full++ <= 0) v_collector__threshold = 0;
	}
	f_wait();
	f_wait();
	f_wait();
	f_wait();
	{
		std::unique_lock<std::mutex> lock(v_collector__conductor.v_mutex);
		if (--v_collector__full <= 0) v_collector__threshold = v_options.v_collector__threshold;
	}
}

void t_engine::f_finalize()
{
	std::unique_lock<std::mutex> lock(v_finalizer__conductor.v_mutex);
	v_finalizer__conductor.f__wake();
	v_finalizer__conductor.f__wait(lock);
}

}
