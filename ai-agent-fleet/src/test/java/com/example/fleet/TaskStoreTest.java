package com.example.fleet;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.file.Path;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class TaskStoreTest {

    @TempDir
    Path directory;

    @Test
    void persistsResultsAndAllocatesNewIds() {
        Path database = directory.resolve("fleet.db");
        TaskStore first = new TaskStore(database);
        Task task = first.create("prompt");
        assertEquals(task, first.claim("agent-1"));
        assertTrue(first.complete(task, "answer"));

        TaskStore reopened = new TaskStore(database);
        assertEquals(new TaskStore.TaskSnapshot("completed", "answer", 0), reopened.snapshot(task.id()));
        assertNotEquals(task.id(), reopened.create("another prompt").id());
    }

    @Test
    void recoversWorkAfterItsOwnerLeaseExpires() throws Exception {
        Path database = directory.resolve("recovery.db");
        TaskStore original = new TaskStore(database, 1);
        Task task = original.create("recover me");
        assertEquals(task, original.claim("agent-1"));
        Thread.sleep(10);

        TaskStore restarted = new TaskStore(database, 1);
        assertEquals(task, restarted.claim("agent-2"));
    }

    @Test
    void onlyOneConcurrentFleetCanClaimATask() throws Exception {
        Path database = directory.resolve("concurrent.db");
        TaskStore first = new TaskStore(database);
        TaskStore second = new TaskStore(database);
        Task task = first.create("one claim");
        CountDownLatch start = new CountDownLatch(1);
        try (ExecutorService workers = Executors.newFixedThreadPool(2)) {
            Future<Task> firstClaim = workers.submit(() -> {
                start.await();
                return first.claim("agent-a");
            });
            Future<Task> secondClaim = workers.submit(() -> {
                start.await();
                return second.claim("agent-b");
            });
            start.countDown();
            Task a = firstClaim.get(5, TimeUnit.SECONDS);
            Task b = secondClaim.get(5, TimeUnit.SECONDS);
            assertNotEquals(a == null ? null : a.id(), b == null ? null : b.id());
            assertEquals(1, (a == null ? 0 : 1) + (b == null ? 0 : 1));
            assertEquals(task.id(), a == null ? b.id() : a.id());
        }
    }

    @Test
    void deadLetterStateIsDurable() {
        TaskStore store = new TaskStore(directory.resolve("dead.db"));
        Task task = store.create("always fails");
        assertEquals(task, store.claim("agent-1"));
        assertEquals(TaskStore.FailureOutcome.DEAD_LETTERED, store.fail(task, "failure", 1));
        assertEquals(new TaskStore.TaskSnapshot("dead", null, 0), store.snapshot(task.id()));
        assertNull(store.claim("agent-2"));
    }

    @Test
    void retryAttemptAndBackoffSurviveRestart() throws Exception {
        Path database = directory.resolve("retry.db");
        TaskStore first = new TaskStore(database);
        Task task = first.create("retry me");
        assertEquals(task, first.claim("agent-1"));
        assertEquals(TaskStore.FailureOutcome.RETRIED, first.fail(task, "temporary", 4));

        TaskStore reopened = new TaskStore(database);
        TaskStore.TaskSnapshot snapshot = reopened.snapshot(task.id());
        assertEquals("pending", snapshot.status());
        assertEquals(1, snapshot.attempts());
        assertNull(reopened.claim("agent-2"));
    }
}
